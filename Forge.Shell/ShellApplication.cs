using Microsoft.Extensions.DependencyInjection;

using Forge.Common.Commands;
using Forge.Common.Debug;
using Forge.Common.Editor;
using Forge.Common.Extensions;
using Forge.Common.Language;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Common.Workspace;
using Forge.Common;
using Forge.Core.Native;
using Forge.Core;
using Forge.Debug;
using Forge.Editor;
using Forge.Extensions.Discord;
using Forge.Extensions.Host;
using Forge.Extensions.TrailingSpaces;
using Forge.Git;
using Forge.Language;
using Forge.Render;
using Forge.Shell.Actions;
using Forge.Terminal;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.UI.Framework;
using Forge.UI;
using Forge.Workspace;
using Forge.VSCode;

namespace Forge.Shell;

[ForgeComponent]
public sealed class ShellApplication(ForgeBuilder builder) : IForgeApplication
{
	public static void Link(IServiceCollection services) => services.AddSingleton<IForgeApplication, ShellApplication>();

	public int Run(string[] args) {
		try {
			return Boot(args);
		}
		catch (Exception exception) {
			ReportFatal(exception);
			return 1;
		}
	}

	private int Boot(string[] args) {
		AppIdentity.Apply();

		AppWindow.FatalHandler = ReportFatal;

		Settings settings = Settings.Load(UserData.SettingsPath);
		WindowState windowState = WindowState.Load(UserData.WindowStatePath);

		using AppWindow window = new("Forge", 1280, 800, windowState);
		using RenderDevice device = new(window.Handle, window.ClientWidth, window.ClientHeight, window.Dpi);

		float scale = window.DpiScale * settings.ZoomFactor;

		using FontSet fonts = new(
			device.DWriteFactory,
			settings.EditorFontFamily, settings.EditorFontSize, settings.EditorLineHeight,
			settings.UiFontFamily, settings.UiFontSize,
			Metrics.FontSizeLabel,
			Path.Combine(AppContext.BaseDirectory, "Assets", "codicon.ttf"),
			scale);

		using Painter painter = new(device, fonts);

		System.Collections.Concurrent.ConcurrentQueue<Action> uiWork = new();

		using DebugService debugger = new();

		using Ambient ambient = new();

		using FileIconSet fileIcons = new(device.Factory,
			Path.Combine(AppContext.BaseDirectory, "Assets"));

		painter.FileIcons = fileIcons;

		using Image appIcon = new(device,
			Path.Combine(AppContext.BaseDirectory, "Assets", "forge-icon.bgra"));

		using Workbench workbench = new(scale) {
			ShowMinimap = settings.MinimapEnabled,
			PaintBackdrop = () => ambient.Composite(device),
		};
		workbench.TitleBar.Icon = appIcon;

		using VectorIcon? explorerIcon = VectorIcon.Load(device.Factory, Path.Combine(AppContext.BaseDirectory, "Assets", "ui", "folder.svg"));
		workbench.ActivityBar.ExplorerIcon = explorerIcon;
		workbench.Auxiliary.IconLoader = path => VectorIcon.Load(device.Factory, path);

		ProcessTree processTree = new();
		workbench.ProcessExplorer = new ProcessExplorer(processTree);

		using Timer resourceTimer = new(_ => {
			workbench.StatusBar.Resources = processTree.Sample();
			window.Invalidate(workbench.StatusBarRect(device.Width, device.Height));

			if (!workbench.ProcessExplorer.IsOpen) return;

			uiWork.Enqueue(workbench.ProcessExplorer.Refresh);
			window.Invalidate();
		}, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));

		string rootPath = ResolveWorkspaceRoot(args);
		workbench.RootPath = rootPath;
		workbench.Panel.WorkingDirectory = rootPath;
		workbench.Panel.DefaultProfile = settings.DefaultTerminalProfile;
		workbench.Panel.History = new TerminalHistory(UserData.TerminalHistoryPath);

		workbench.Panel.Changed += () =>
			window.Invalidate(workbench.PanelRect(device.Width, device.Height));
		workbench.Panel.Output.Append($"Workspace: {rootPath}");
		workbench.BranchName = GitWorkspaceInfo.ReadBranchName(rootPath);
		workbench.TitleBar.BranchName = workbench.BranchName;

		workbench.Repository = GitRepository.Open(rootPath);
		workbench.Sidebar.Repository = workbench.Repository;
		workbench.Sidebar.SourceControl.SetRoot(workbench.Repository?.GetRootPath() ?? rootPath);
		workbench.Sidebar.SourceControl.LoadState(UserData.SourceControlStatePath);

		if (workbench.Repository is { } repository) {
			repository.OnChanged(() => {
				workbench.BranchName = repository.GetBranch() ?? workbench.BranchName;
				workbench.TitleBar.BranchName = workbench.BranchName;
				window.Invalidate();
			});

			ThreadPool.UnsafeQueueUserWorkItem(_ => repository.Refresh(), null);

			workbench.ActiveDocumentChanged += () => workbench.RefreshDiff(window.Invalidate);
		}

		if (GitWorkspaceInfo.ReadRemote(rootPath) is { } remote) {
			workbench.TitleBar.RemoteOwner = remote.Owner;
			workbench.TitleBar.RemoteRepository = remote.Repository;
		}
		workbench.Sidebar.Title = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar));
		workbench.Sidebar.SetTree(new FileTree(rootPath));

		debugger.Attach(rootPath, UserData.BreakpointsPath);

		WorkspaceMemory memory = WorkspaceMemory.Load(rootPath);

		if (memory.DebugConfiguration is { Length: > 0 } remembered) {
			foreach (DebugConfiguration configuration in debugger.GetConfigurations()) {
				if (!string.Equals(configuration.Name, remembered, StringComparison.Ordinal)) continue;

				debugger.SelectConfiguration(remembered);
				break;
			}
		}

		debugger.ContextProvider = () => new ResolverContext(rootPath) {
			File = workbench.ActiveFilePath,
			LineNumber = (workbench.ActiveView?.CaretLine ?? 0) + 1,
			ColumnNumber = (workbench.ActiveView?.CaretColumn ?? 0) + 1,
		};

		debugger.TaskRunner = (label, commandLine, cwd, finished) => {
			ambient.Begin(AmbientSignal.Build);

			Post(() => workbench.Panel.RunTask(label, commandLine, cwd ?? rootPath, null, exitCode => {
				ambient.End(AmbientSignal.Build, exitCode == 0);
				finished?.Invoke(exitCode);
				window.Invalidate();
			}));

			return true;
		};

		void Post(Action action) {
			uiWork.Enqueue(action);
			window.Invalidate();
		}

		debugger.RevealSource = (path, line) => Post(() => {
			workbench.OpenFile(path);

			if (workbench.ActiveView is { } view)
				view.MoveTo(view.Snapshot.GetLineStartOffset(Math.Max(line - 1, 0)), extend: false);

			window.Invalidate();
		});

		using Forge.Webview.WebviewHost webviews = new(window.Handle, device, Path.Combine(UserData.Directory, "webview"), Post);

		workbench.Webviews = webviews;
		workbench.Auxiliary.Webviews = webviews;

		webviews.SetZoom(settings.ZoomFactor);

		webviews.ThemeCss = Common.Webview.WebviewTheme.BuildCss(settings.EditorFontFamily[0],
			settings.EditorFontSize);

		webviews.OnFailure(reason => {
			workbench.Panel.Channel("Extensions").Append($"Webview: {reason}");
			window.Invalidate();
		});

		webviews.OnMessage((viewId, json) => {
			if (workbench.VSCode is not { } vscode) return;

			try {
				_ = vscode.PostWebviewMessageAsync(viewId, System.Text.Json.Nodes.JsonNode.Parse(json));
			}
			catch (System.Text.Json.JsonException) {
			}
		});

		IOutputChannel debugConsole = workbench.Panel.Channel("Debug Console");

		debugger.OnOutput(value => {
			debugConsole.Append(value.Text.TrimEnd((char)10, (char)13));
			workbench.Panel.MarkDirty();
		});

		bool debugging = false;

		debugger.OnStateChanged(() => {
			bool active = debugger.IsActive();

			if (active != debugging) {
				debugging = active;

				if (active) ambient.Begin(AmbientSignal.Debug);
				else ambient.End(AmbientSignal.Debug);
			}

			window.Invalidate();
		});

		void SyncLaunchControl() {
			List<string> names = [];

			foreach (DebugConfiguration configuration in debugger.GetConfigurations()) {
				names.Add(configuration.Name);
			}

			workbench.TitleBar.DebugConfigurations = names;
			workbench.TitleBar.SelectedDebugConfiguration = debugger.GetSelectedConfiguration();
			workbench.TitleBar.IsDebugging = debugger.IsActive();
			workbench.TitleBar.IsPaused = debugger.GetState() == DebugState.Stopped;
		}

		SyncLaunchControl();

		debugger.OnModelChanged(() => {
			SyncLaunchControl();
			window.Invalidate();
		});

		debugger.OnStateChanged(() => {
			SyncLaunchControl();
			window.Invalidate();
		});

		workbench.Sidebar.Debug.Debugger = debugger;
		workbench.Sidebar.Debug.ScopesLoader = debugger.ScopesAsync;
		workbench.Sidebar.Debug.VariablesLoader = debugger.VariablesAsync;
		workbench.Sidebar.Debug.Changed = window.Invalidate;
		workbench.Sidebar.Debug.OpenBreakpoint = (path, line) => debugger.RevealSource?.Invoke(path, line);

		workbench.BreakpointMarkFor = line => {
			if (workbench.ActiveFilePath is not { Length: > 0 } path) return BreakpointMark.None;

			if (debugger.FindBreakpoint(path, line) is { } breakpoint) {
				if (!breakpoint.Enabled || !debugger.AreBreakpointsActivated()) return BreakpointMark.Disabled;
				if (breakpoint.LogMessage is { Length: > 0 }) return BreakpointMark.LogPoint;
				if (breakpoint.Condition is { Length: > 0 }) return BreakpointMark.Conditional;

				return debugger.IsActive() && !breakpoint.Verified
					? BreakpointMark.Unverified
					: BreakpointMark.Verified;
			}

			return BreakpointMark.None;
		};

		workbench.ExecutionLineFor = path => {
			if (!debugger.IsActive() || debugger.GetState() != DebugState.Stopped) return -1;

			return debugger.GetFocusedFrame() is { SourcePath: { } source } frame &&
				string.Equals(source, path, StringComparison.OrdinalIgnoreCase)
					? frame.Line - 1
					: -1;
		};

		DebugCommands.Register(Registry, debugger);

		ThreadPool.UnsafeQueueUserWorkItem(_ => LoadExtensions(workbench, window.Invalidate, Post), null);

		IFileIndex index = new FileIndex(rootPath);
		workbench.QuickOpen.Attach(index);

		index.OnUpdated(() => {
			ambient.Pulse(AmbientSignal.Search, 0.55f);

			if (workbench.QuickInput.IsOpen && workbench.QuickInput.Provider == workbench.QuickOpen) Post(workbench.QuickInput.Refilter);
		});

		fileIcons.OnReady(window.Invalidate);

		window.Events.PlacementChanged += () => window.CaptureState().Save(UserData.WindowStatePath);

		index.StartScan();

		using WorkspaceWatcher watcher = new(rootPath, (directories, git) => {
			if (git && workbench.Repository is { } watched) watched.Refresh();

			if (directories.Count > 0) Post(() => workbench.Sidebar.Explorer.Reload(directories));
		});

		RestoreWorkspace(workbench, memory);
		OpenInitialFiles(workbench, args);
		UpdateTitle(window, workbench, rootPath);

		WireEvents(window, device, painter, fonts, settings, workbench, ambient, debugger, uiWork, rootPath);

		builder
			.Publish<IAppWindow>(window)
			.Publish<IRenderDevice>(device)
			.Publish<IFontSet>(fonts)
			.Publish<IFileIconSet>(fileIcons)
			.Publish<IPainter>(painter)
			.Publish<IEditorHost>(workbench)
			.Publish<IFileIndex>(index)
			.Publish<IAmbient>(ambient)
			.Publish<IDebug>(debugger);

		if (workbench.Repository is { } published) builder.Publish(published);

		if (workbench.IntelliSense is { } completion) builder.Publish<IIntelliSense>(completion);

		builder.Inject();

		AppDomain.CurrentDomain.ProcessExit += (_, _) => workbench.ExtensionHost?.UnloadAll();

		window.Show();

		try {
			return AppWindow.RunMessageLoop();
		}
		finally {
			index.Cancel();

			workbench.ExtensionHost?.UnloadAll();

			window.CaptureState().Save(UserData.WindowStatePath);
			CaptureWorkspace(workbench, debugger, memory);
			memory.Save();
			settings.Save();
		}
	}

	private static void RestoreWorkspace(Workbench workbench, WorkspaceMemory memory) {
		workbench.ShowSidebar = memory.ShowSidebar;

		if (memory.SidebarWidth >= Metrics.SidebarMinWidth) workbench.Sidebar.Width = memory.SidebarWidth;

		if (Enum.TryParse(memory.SidebarView, out ActivityView view)) {
			workbench.Sidebar.View = view;
			workbench.ActivityBar.Active = view;
		}

		if (memory.PanelHeight > 0f) workbench.Panel.Height = memory.PanelHeight;

		if (memory.AuxiliaryWidth >= AuxiliaryBar.MinWidth) workbench.Auxiliary.Width = memory.AuxiliaryWidth;

		workbench.Auxiliary.IsOpen = memory.AuxiliaryOpen;

		if (memory.AuxiliaryView is { Length: > 0 } auxiliaryView) {
			workbench.Auxiliary.RestoreSelection(auxiliaryView);
		}

		if (memory.PanelOpen && Enum.TryParse(memory.PanelView, out PanelView panel)) {
			workbench.Panel.Show(panel);
		}

		foreach (string path in memory.OpenFiles) {
			if (File.Exists(path)) workbench.OpenFile(path);
		}

		if (memory.ActiveFile is { Length: > 0 } active && File.Exists(active)) workbench.OpenFile(active);
	}

	private static void CaptureWorkspace(Workbench workbench, DebugService debugger, WorkspaceMemory memory) {
		memory.OpenFiles.Clear();

		foreach (Document document in workbench.Documents) {
			if (document.Path is { Length: > 0 } path) memory.OpenFiles.Add(path);
		}

		memory.ActiveFile = workbench.ActiveFilePath;
		memory.ShowSidebar = workbench.ShowSidebar;
		memory.SidebarWidth = workbench.Sidebar.Width;
		memory.SidebarView = workbench.Sidebar.View.ToString();
		memory.PanelOpen = workbench.Panel.IsOpen;
		memory.PanelView = workbench.Panel.Active.ToString();
		memory.PanelHeight = workbench.Panel.Height;
		memory.AuxiliaryOpen = workbench.Auxiliary.IsOpen;
		memory.AuxiliaryWidth = workbench.Auxiliary.Width;
		memory.AuxiliaryView = workbench.Auxiliary.ActiveViewId;
		memory.DebugConfiguration = debugger.GetSelectedConfiguration();
	}

	private static string ResolveWorkspaceRoot(string[] args) {
		foreach (string argument in args) {
			if (Directory.Exists(argument)) return Path.GetFullPath(argument);

			if (File.Exists(argument)) {
				return Path.GetDirectoryName(Path.GetFullPath(argument)) ?? Environment.CurrentDirectory;
			}
		}

		return Environment.CurrentDirectory;
	}

	private static void OpenInitialFiles(Workbench workbench, string[] args) {
		foreach (string argument in args) {
			if (File.Exists(argument)) workbench.OpenFile(Path.GetFullPath(argument));
		}
	}


	private static Document? _titledDocument;
	private static bool _titledDirty;
	private static bool _titled;

	private static void UpdateTitle(AppWindow window, Workbench workbench, string rootPath) {
		Document? document = workbench.ActiveDocument;
		bool dirty = document?.IsDirty() ?? false;

		if (_titled && ReferenceEquals(document, _titledDocument) && dirty == _titledDirty) return;

		_titled = true;
		_titledDocument = document;
		_titledDirty = dirty;

		string title = WindowTitle.Format(Settings.Current.GetString("window.title"), rootPath, document?.Path, document?.IsDirty() ?? false);

		workbench.TitleBar.FolderName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar));
		workbench.TitleBar.Title = title;
		window.SetTitle(title);
	}


	private static void WireEvents(AppWindow window, RenderDevice device, IPainter painter, FontSet fonts,
		Settings settings, Workbench workbench, Ambient ambient, DebugService debugger,
		System.Collections.Concurrent.ConcurrentQueue<Action> uiWork, string rootPath) {
		DamageRegion pending = new();
		DamageRegion frameDamage = new();
		DamageRegion editorDamage = new();

		Action<PixelRect>? notePaint = null;

		bool frameRequested = false;

		void Damage(PixelRect rect) {
			pending.Add(rect);
			notePaint?.Invoke(rect);

			if (frameRequested) return;

			frameRequested = true;
			window.Invalidate(new PixelRect(0, 0, 1, 1));
		}

		void Refresh() {
			UpdateTitle(window, workbench, rootPath);
			Damage(PixelRect.FromSize(device.Width, device.Height));
		}

		static KeyModifiers Modifiers() => (Win32.IsKeyDown(Win32.VkControl) ? KeyModifiers.Control : 0) | (Win32.IsKeyDown(Win32.VkShift) ? KeyModifiers.Shift : 0) | (Win32.IsKeyDown(Win32.VkMenu) ? KeyModifiers.Alt : 0);

		UiSystem ui = new(Damage, () => window.Invalidate(new PixelRect(0, 0, 1, 1)), () => workbench.Scale, Modifiers) { Fonts = fonts };

		notePaint = ui.NotePaint;

		workbench.ActivityBar.ViewSelected = view => {
			if (workbench.ActivityBar.Active == view) workbench.ShowSidebar = !workbench.ShowSidebar;
			else {
				workbench.ActivityBar.Active = view;
				workbench.ShowSidebar = true;
			}

			workbench.Sidebar.View = view;

			if (view == ActivityView.SourceControl) {
				workbench.Sidebar.SourceControl.SetRoot(workbench.Repository?.GetRootPath() ?? rootPath);
				Registry.Execute("git.refresh", new CommandHost(workbench, Refresh));
			}

			Refresh();
		};
		WorkbenchPanel root = new(workbench);
		ui.Root.AddChild(root);
		ui.CommandHandler = (command, _) => workbench.RunCommand(command, new CommandHost(workbench, Refresh));


		void RefreshEditor() {
			UpdateTitle(window, workbench, rootPath);

			editorDamage.Clear();
			workbench.AddEditorDamage(editorDamage, painter, device.Width, device.Height);

			for (int i = 0; i < editorDamage.Count; i++) Damage(editorDamage[i]);
		}

		workbench.IntelliSense = new IntelliSense(workbench, Refresh) {
			DetailsVisibleChanged = settings.SetSuggestDetailsVisible,
		};

		workbench.IntelliSense.Suggest.DetailsVisible = settings.SuggestDetailsVisible;

		void ApplyZoom() {
			float target = window.DpiScale * settings.ZoomFactor;

			workbench.Webviews.SetZoom(settings.ZoomFactor);

			if (!fonts.Rescale(target)) return;

			workbench.Scale = target;
			workbench.ActiveView?.EnsureCaretVisible(painter.GetCode(), workbench.TextWidth(device.Width),
				workbench.EditorHeight(device.Height));

			Refresh();
		}

		ShellContext context = new(workbench, window, device, painter, settings, debugger, rootPath) {
			Refresh = Refresh,
			RefreshEditor = RefreshEditor,
			ApplyZoom = ApplyZoom,
			Damage = Damage,
			SetCodeZoom = fonts.SetCodeZoom,
		};

		workbench.Sidebar.Explorer.FileOpened = path => {
			workbench.OpenFile(path);
			Refresh();
		};

		workbench.Auxiliary.Activated = Refresh;

		root.Editor.Changed = RefreshEditor;
		root.Editor.Pressed = Refresh;
		root.Editor.WheelZooms = () => settings.MouseWheelZoom;
		root.Editor.ZoomRequested = step => context.ZoomEditor(step);

		root.Editor.BreakpointToggled = line => {
			if (workbench.ActiveFilePath is { Length: > 0 } path) debugger.ToggleBreakpoint(path, line);

			Refresh();
		};
		workbench.Find.View = () => workbench.ActiveView;

		workbench.Find.Revealed = found => {
			context.ScrollToMatch(found);
			RefreshEditor();
		};
		workbench.FocusEditor = root.Editor.RequestFocus;
		root.Editor.Typed = context.RevealCaret;
		root.Editor.RequestFocus();

		workbench.Panel.Navigate = (path, line, column) => {
			workbench.GoTo(path, line, column);
			Refresh();
		};

		workbench.Tabs.Activated = index => {
			workbench.Activate(index);
			Refresh();
		};

		workbench.Tabs.Closed = index => {
			workbench.Close(index);
			Refresh();
		};

		GitCommands.Register(Registry, context);
		WorkbenchCommands.Register(Registry, context);
		EditorCommands.Register(Registry, context);
		InputCommands.Register(Registry, context);
		DeveloperCommands.Register(Registry, ui);

		ui.KeybindingLookup = id => Registry.Find(id)?.GetKeybinding();
		ui.EnabledLookup = id => Registry.Find(id)?.IsEnabled() ?? false;

		IFrameStats stats = new FrameStats();
		workbench.FrameStats = stats;


		window.HitTestTitleBar = (x, y) => {
			workbench.TitleBar.IsMaximized = window.IsMaximized;
			return workbench.TitleBar.HitTest(x, y);
		};

		const nuint caretTimerId = 1;
		const nuint settleTimerId = 3;
		const nuint ambientTimerId = 4;
		const uint ambientIntervalMs = 16;

		window.StartTimer(caretTimerId, Math.Max(100u, AppWindow.CaretBlinkMs));

		window.StartTimer(settleTimerId, 5000u);
		window.StartTimer(ambientTimerId, ambientIntervalMs);

		long ambientStamp = System.Diagnostics.Stopwatch.GetTimestamp();

		void SyncWebviews() {
			AuxiliaryBar bar = workbench.Auxiliary;

			if (workbench.AuxiliaryWidth <= 0f || bar.Active is not { } entry) {
				if (bar.ShownViewId is { } hidden) {
					_ = workbench.VSCode?.SetWebviewVisibilityAsync(hidden, false) ?? Task.CompletedTask;
					bar.ShownViewId = null;
				}

				workbench.Webviews.HideAll();
				return;
			}

			if (bar.HtmlFor(entry.ViewId) is not { Length: > 0 } html) {
				if (!bar.ClaimResolve(entry.ViewId) || workbench.VSCode is not { } vscode) return;

				bar.Status = "Loading view...";

				_ = vscode.ResolveWebviewAsync(entry.ViewId, entry.Title, entry.Directory)
					.ContinueWith(task => {
						if (task.Result is not { Length: > 0 } resolved) return;

						uiWork.Enqueue(() => {
							bar.SetHtml(entry.ViewId, resolved);
							bar.Status = null;
						});

						window.Invalidate();
					}, TaskScheduler.Default);

				return;
			}

			if (bar.ContentBounds is not { Right: > 0, Bottom: > 0 } bounds) return;

			if (string.Equals(bar.ShownViewId, entry.ViewId, StringComparison.Ordinal)) {
				workbench.Webviews.SetBounds(entry.ViewId, bounds);
				return;
			}

			if (bar.ShownViewId is { } previous) {
				workbench.Webviews.Hide(previous);
				_ = workbench.VSCode?.SetWebviewVisibilityAsync(previous, false) ?? Task.CompletedTask;
			}

			bar.ShownViewId = entry.ViewId;
			_ = workbench.VSCode?.SetWebviewVisibilityAsync(entry.ViewId, true) ?? Task.CompletedTask;
			bar.Status = null;
			workbench.Webviews.Show(entry.ViewId, html, entry.Directory, bounds, entry.ExtensionId);
		}

		void AmbientTick() {
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			float delta = (float)((now - ambientStamp) / (double)System.Diagnostics.Stopwatch.Frequency);
			ambientStamp = now;

			ui.RunFrame(delta);

			SyncWebviews();

			while (uiWork.TryDequeue(out Action? work)) {
				try {
					work();
				}
				catch (Exception exception) when (exception is InvalidOperationException or IOException) {
					workbench.Panel.Output.Append($"Debug UI update failed: {exception.Message}");
				}
			}

			if (ambient.Tick(delta)) ambient.Damage(window.Invalidate, device.Width, device.Height);

			if (workbench.Panel.TakeDirty()) workbench.Panel.Repaint();
		}

		window.Events.Timer += id => {
			if (id == ambientTimerId) {
				AmbientTick();
				return;
			}

			if (id == settleTimerId) {
				window.StopTimer(settleTimerId);

				System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
					System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;

				GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
				return;
			}

			if (id != caretTimerId ) return;

			Caret.Visible = !Caret.Visible;

			if (workbench.ActiveView is not { } blinking) return;

			blinking.CaretVisible = !blinking.CaretVisible;

			Damage(blinking.CaretRect(painter.GetCode(), workbench.EditorLeft, workbench.EditorTop,
				workbench.TextWidth(device.Width), workbench.EditorHeight(device.Height)));
		};

		string? lastRenderFailure = null;

		bool rendering = false;

		window.Events.Render += windowsDamage => {
			if (rendering) return;

			rendering = true;

			try {
				RenderFrame(windowsDamage);
			}
			finally {
				rendering = false;

				if (!pending.IsEmpty) {
					frameRequested = true;
					window.Invalidate(new PixelRect(0, 0, 1, 1));
				}
			}
		};

		void RenderFrame(PixelRect windowsDamage) {
			frameRequested = false;
			PixelRect surface = PixelRect.FromSize(device.Width, device.Height);

			if (!pending.Bounds().Contains(windowsDamage)) pending.Add(windowsDamage);

			if (!pending.IsEmpty && workbench.ShowFrameStats) pending.Add(workbench.FrameStatsOverlay.AbsoluteBounds);

			frameDamage.Clear();
			frameDamage.Add(pending);
			pending.Clear();

			long surfaceArea = (long)surface.Width * surface.Height;

			stats.BeginFrame(surfaceArea > 0 ? (double)frameDamage.Area() / surfaceArea : 1.0);

			if (frameDamage.IsEmpty) {
				stats.EndFrame();
				return;
			}

			workbench.UpdateBeforeFrame();
			root.SyncLegacyLayout(device.Width, device.Height);
			ui.Layout(device.Width, device.Height);

			ambient.Prepare(device);

			device.BeginDraw();

			for (int pass = 0; pass < frameDamage.Count; pass++) {
				PixelRect repaint = frameDamage[pass].Intersect(surface);

				if (repaint.IsEmpty) continue;

				painter.BeginFrame(repaint);
				painter.PushClip(repaint.Left, repaint.Top, repaint.Width, repaint.Height);

				device.Clear(Theme.EditorBackground);

				try {
					ui.Paint(painter, repaint);
				}
				catch (Exception exception) {
					painter.UnwindClips();

					string message = exception.ToString();

					if (message != lastRenderFailure) {
						lastRenderFailure = message;
						workbench.Panel.Output.Append($"Render failed: {message}");
					}
				}

				painter.PopClip();
			}

			device.EndDraw();

			if (ambient.LastFailure is { } ambientFailure && ambientFailure != lastRenderFailure) {
				lastRenderFailure = ambientFailure;
				workbench.Panel.Output.Append($"Ambient background failed: {ambientFailure}");
			}

			if (device.LastFailure is { } failure && failure != lastRenderFailure) {
				lastRenderFailure = failure;
				workbench.Panel.Output.Append($"Direct2D dropped a frame: {failure}");
			}

			painter.EndFrame();

			stats.EndFrame();
		}

		window.Events.Resized += (width, height) => {
			device.Resize(width, height, window.Dpi);

			workbench.TitleBar.IsMaximized = window.IsMaximized;
			window.Invalidate();
		};

		window.Events.DpiChanged += _ => ApplyZoom();

		window.Events.TitleBarMouseMove += (x, y) => ui.InternalCursorMoved(x, y);
		window.Events.TitleBarMouseLeave += ui.InternalCursorExited;
		window.Events.PointerLeftClient += ui.DismissPopups;

		window.Events.MouseWheel += (delta, x, _) => {
			if (ui.InternalMouseWheeled(delta, x, LastMouseY) || !Win32.IsKeyDown(Win32.VkControl)) return;

			settings.AdjustZoom(delta > 0 ? 0.2f : -0.2f);
			ApplyZoom();
		};

		window.Events.MouseHorizontalWheel += (delta, _, _) => {
			workbench.ActiveView?.ScrollByHorizontalWheel(delta, painter.GetCode().GetCellWidth());
			RefreshEditor();
		};

		nint arrowCursor = AppWindow.SystemCursor(Win32.IdcArrow);
		nint beamCursor = AppWindow.SystemCursor(Win32.IdcIBeam);
		nint handCursor = AppWindow.SystemCursor(Win32.IdcHand);
		nint resizeWidthCursor = AppWindow.SystemCursor(Win32.IdcSizeWestEast);
		nint resizeHeightCursor = AppWindow.SystemCursor(Win32.IdcSizeNorthSouth);

		window.Cursor = arrowCursor;

		ui.CursorChanged = code => window.Cursor = ui.GetMouseOver() is WebviewSurface && workbench.Webviews.GetCursor() is not 0 and nint webCursor ? webCursor : code switch {
			CursorCode.IBeam => beamCursor,
			CursorCode.Hand => handCursor,
			CursorCode.SizeWestEast => resizeWidthCursor,
			CursorCode.SizeNorthSouth => resizeHeightCursor,
			_ => arrowCursor,
		};

		workbench.Webviews.OnCursorChanged(() => {
			if (ui.GetMouseOver() is WebviewSurface) window.Cursor = workbench.Webviews.GetCursor();
		});

		root.Resized = Refresh;

		window.Events.MouseMove += (x, y) => {
			LastMouseX = x;
			LastMouseY = y;

			if (!ui.InternalCursorMoved(x, y)) window.Cursor = arrowCursor;
		};

		window.Events.MouseDown += (x, y) => {
			if (workbench.Auxiliary.ShownViewId is not null && !workbench.Auxiliary.ContentBounds.Contains(new PixelRect(x, y, x + 1, y + 1))) workbench.Webviews.ReleaseFocus();

			if (ui.InternalMousePressed(MouseButton.Left, x, y)) return;

			workbench.IntelliSense?.Dismiss();
			workbench.PanelFocused = false;
		};

		window.Events.MouseUp += (x, y) => ui.InternalMouseReleased(MouseButton.Left, x, y);

		ui.KeyFallback = (key, modifiers) => {
			if (Registry.Resolve(key, (modifiers & KeyModifiers.Control) != 0, (modifiers & KeyModifiers.Shift) != 0, (modifiers & KeyModifiers.Alt) != 0) is not { } command) return false;

			workbench.RunCommand(command.GetId(), context.Host);
			return true;
		};

		window.Events.CharacterTyped += character => {
			if (character < ' ' || character == 0x7F || Win32.IsKeyDown(Win32.VkControl)) return;

			ui.InternalKeyTyped(character);
		};

		window.Events.KeyDown += key => ui.InternalKeyCodePressed(key, Modifiers());
	}

	private static void LoadExtensions(Workbench workbench, Action refresh, Action<Action> post) {
		CommandHost host = new(workbench, refresh);

		ExtensionHost extensions = new(Settings.Current, BuiltInExtensions.All);
		workbench.ExtensionHost = extensions;

		int built = extensions.RegisterAll(Registry);

		TrailingSpacesExtension trailing = BuiltInExtensions.Get<TrailingSpacesExtension>();

		workbench.TrailingSpaces = trailing;
		workbench.Presence = BuiltInExtensions.Get<DiscordPresenceExtension>();

		workbench.WillSave = text => {
			if (!trailing.TrimOnSave) return null;

			List<TrailingRange> ranges =
				TrailingSpacesExtension.Find(text, skipLine: null, includeEmptyLines: true);

			return ranges.Count == 0 ? null : TrailingSpacesExtension.Apply(text, ranges);
		};

		workbench.Panel.Output.Append($"Extensions: {BuiltInExtensions.All.Count} built in, {built} commands");

		extensions.Startup(host);
		workbench.FileOpened += path => extensions.FileOpened(host, path);
		workbench.CommandInvoked += id => extensions.CommandInvoked(host, id);

		workbench.Panel.Output.Append($"Extensions: {extensions.Active().Count} activated");

		IExtensionRegistry registry = new ExtensionRegistry();
		registry.ScanDirectory(UserData.ExtensionsDirectory);

		workbench.Extensions = registry;

		if (registry.All().Count > 0) {
			workbench.Panel.Output.Append(
				$"Extensions: {registry.All().Count} installed, " +
				$"{registry.RequiringHost().Count()} needing an extension host");
		}

		StartVSCodeBridge(workbench, host, post);
	}

	private static void StartVSCodeBridge(Workbench workbench, CommandHost host, Action<Action> post) {
		VSCodeBridge bridge = new(Registry, host);
		workbench.VSCode = bridge;

		bridge.ViewsChanged = () => post(() => workbench.Auxiliary.SetViews(bridge.WebviewViews));

		bridge.WebviewHtmlChanged = (viewId, html) => post(() => {
			workbench.Auxiliary.SetHtml(viewId, html);

			if (string.Equals(workbench.Auxiliary.ShownViewId, viewId, StringComparison.Ordinal)) {
				workbench.Auxiliary.ShownViewId = null;
			}
		});

		bridge.WebviewMessage = (viewId, json) => post(() => workbench.Webviews.PostMessage(viewId, json));

		HostState state = new() {
			WorkspacePath = workbench.RootPath,
			ActiveEditor = EditorStateFor(workbench),
		};

		bridge.Start(
			Path.Combine(UserData.Directory, "vscode-storage"),
			state,
			VSCodeExtensions.DefaultDirectory);
	}

	private static HostEditor? EditorStateFor(Workbench workbench) {
		if (workbench.ActiveDocument?.Path is not { } path || workbench.ActiveView is not { } view) return null;

		return new HostEditor(path, view.CaretLine, view.CaretColumn, view.CaretLine, view.CaretColumn);
	}



	private static int LastMouseX { get; set; }

	private static int LastMouseY { get; set; }

	private static void ReportFatal(Exception exception) {
		string report = exception.ToString();

		try {
			string path = Path.Combine(AppContext.BaseDirectory, "forge-crash.log");
			File.AppendAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{report}{Environment.NewLine}{Environment.NewLine}");
		}
		catch {
		}

		Win32.MessageBox(0, report, "Forge failed", Win32.MbIconError);
	}
}
