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

		using Forge.Webview.WebviewHost webviews = new(window.Handle,
			Path.Combine(UserData.Directory, "webview"), Post);

		workbench.Webviews = webviews;

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

		debugger.OnStateChanged(() => {
			if (debugger.GetState() != DebugState.Stopped) workbench.Sidebar.Debug.Reset();
		});

		workbench.BreakpointMarkFor = line => {
			if (workbench.ActiveFilePath is not { Length: > 0 } path) return BreakpointMark.None;

			foreach (SourceBreakpoint breakpoint in debugger.GetBreakpointsFor(path)) {
				if (breakpoint.EffectiveLine != line) continue;

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

	private static void UpdateTitle(AppWindow window, Workbench workbench, string rootPath) {
		Document? document = workbench.ActiveDocument;
		string title = WindowTitle.Format(Settings.Current.GetString("window.title"), rootPath, document?.Path, document?.IsDirty() ?? false);

		workbench.TitleBar.FolderName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar));
		workbench.TitleBar.Title = title;
		window.SetTitle(title);
	}


	private static void WireEvents(AppWindow window, RenderDevice device, IPainter painter, FontSet fonts,
		Settings settings, Workbench workbench, Ambient ambient, DebugService debugger,
		System.Collections.Concurrent.ConcurrentQueue<Action> uiWork, string rootPath) {
		bool selecting = false;
		bool draggingSplitter = false;
		bool draggingAuxiliary = false;
		bool draggingPanel = false;
		bool selectingOutput = false;

		int dragX = 0;
		int dragY = 0;

		long lastClickAt = 0;
		int lastClickX = 0;
		int lastClickY = 0;
		int clickCount = 0;
		bool autoScrolling = false;

		DamageRegion pending = new();
		DamageRegion frameDamage = new();
		DamageRegion editorDamage = new();

		Action<PixelRect>? notePaint = null;

		void Damage(PixelRect rect) {
			pending.Add(rect);
			window.Invalidate(rect);
			notePaint?.Invoke(rect);
		}

		void DamageCleared(ChromeRegions cleared) {
			if (cleared.Sidebar) Damage(workbench.SidebarRect(device.Height));

			if (cleared.ActivityBar) Damage(workbench.ActivityBarRect(device.Height));

			if (cleared.Tabs) Damage(workbench.TabStripRect(device.Width));
		}

		void Refresh() {
			UpdateTitle(window, workbench, rootPath);
			Damage(PixelRect.FromSize(device.Width, device.Height));
		}

		static KeyModifiers Modifiers() => (Win32.IsKeyDown(Win32.VkControl) ? KeyModifiers.Control : 0) | (Win32.IsKeyDown(Win32.VkShift) ? KeyModifiers.Shift : 0) | (Win32.IsKeyDown(Win32.VkMenu) ? KeyModifiers.Alt : 0);

		UiSystem ui = new(Damage, () => window.Invalidate(new PixelRect(0, 0, 1, 1)), () => workbench.Scale, Modifiers) { Fonts = fonts };

		notePaint = ui.NotePaint;
		ui.Root.AddChild(new LegacyWorkbenchPanel(workbench));
		ui.CommandHandler = (command, _) => workbench.RunCommand(command, new CommandHost(workbench, Refresh));

		bool uiOwnsPointer = false;

		PopupRects occlusions = default;
		int occlusionCount = 0;

		void SyncOcclusions() {
			PopupRects current = default;
			int count = ui.CopyPopupBounds(current);

			if (count == occlusionCount && ((ReadOnlySpan<PixelRect>)current)[..count].SequenceEqual(((ReadOnlySpan<PixelRect>)occlusions)[..count])) return;

			occlusions = current;
			occlusionCount = count;
			workbench.Webviews.SetOcclusions(((ReadOnlySpan<PixelRect>)occlusions)[..count]);
		}

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
		};

		InputTargets.Register(context);
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
			return workbench.TitleBar.HitTest(x, y, device.Width, workbench.Scale);
		};

		const nuint caretTimerId = 1;
		const nuint autoScrollTimerId = 2;
		const nuint settleTimerId = 3;
		const nuint ambientTimerId = 4;
		const uint ambientIntervalMs = 16;
		const uint autoScrollIntervalMs = 30;

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

			if (workbench.Panel.IsDirty) window.Invalidate(workbench.PanelRect(device.Width, device.Height));
		}

		void AutoScrollTick() {
			if (workbench.ActiveView is not { } view) return;

			float top = workbench.EditorTop;
			float bottom = workbench.StatusBarTop(device.Height);
			float left = workbench.EditorLeft;
			float right = left + workbench.TextWidth(device.Width);

			float lines = Overshoot(dragY, top, bottom) / painter.GetCode().GetLineHeight();
			float columns = Overshoot(dragX, left, right) / painter.GetCode().GetCellWidth();

			if (lines != 0f) {
				view.ScrollByLines(lines, painter.GetCode().GetLineHeight(), workbench.EditorHeight(device.Height));
			}

			if (columns != 0f) view.ScrollByColumns(columns, painter.GetCode().GetCellWidth());

			view.MoveTo(
				view.OffsetFromPoint(painter.GetCode(), Math.Clamp(dragX, left, right),
					Math.Clamp(dragY, top, bottom - 1f), left, top),
				extend: true);

			RefreshEditor();
		}

		void SetAutoScrolling(bool enabled) {
			if (enabled == autoScrolling) return;

			autoScrolling = enabled;

			if (enabled) window.StartTimer(autoScrollTimerId, autoScrollIntervalMs);
			else window.StopTimer(autoScrollTimerId);
		}

		window.Events.Timer += id => {
			if (id == ambientTimerId) {
				AmbientTick();
				return;
			}

			if (id == autoScrollTimerId) {
				AutoScrollTick();
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

			if (workbench.Sidebar.View == ActivityView.SourceControl &&
				workbench.Sidebar.SourceControl.MessageFocused) Damage(workbench.SidebarRect(device.Height));

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
			}
		};

		void RenderFrame(PixelRect windowsDamage) {
			PixelRect surface = PixelRect.FromSize(device.Width, device.Height);

			if (!pending.Bounds().Contains(windowsDamage)) pending.Add(windowsDamage);

			if (!pending.IsEmpty) pending.Add(workbench.FrameStatsRect(device.Width, device.Height));

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
			ui.Layout(device.Width, device.Height);
			SyncOcclusions();

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

		void HoverTitleBar(int x, int y) {
			TitleBar bar = workbench.TitleBar;

			TitleBarPart part = bar.PartAt(x, y, device.Width, workbench.Scale);

			if (part == bar.Hover) return;

			bar.SetHover(part);
			Damage(workbench.TitleBarRect(device.Width));
		}

		window.Events.TitleBarMouseMove += HoverTitleBar;

		window.Events.TitleBarMouseLeave += () => {
			if (workbench.TitleBar.Hover == TitleBarPart.None) return;

			workbench.TitleBar.SetHover(TitleBarPart.None);
			Damage(workbench.TitleBarRect(device.Width));
		};

		window.Events.MouseWheel += (delta, x, _) => {
			if (ui.InternalMouseWheeled(delta, x, LastMouseY)) return;

			if (Win32.IsKeyDown(Win32.VkControl)) {
				settings.AdjustZoom(delta > 0 ? 0.2f : -0.2f);
				ApplyZoom();
				return;
			}

			if (workbench.IntelliSense is { } scrolling &&
				scrolling.Suggest.Contains(x, LastMouseY)) {
				scrolling.Suggest.ScrollByWheel(delta);
				Damage(workbench.EditorCardRect(device.Width, device.Height));
				return;
			}

			if (workbench.Panel.IsOpen && LastMouseY >= workbench.PanelTop(device.Height) &&
				x >= workbench.EditorLeft) {
				workbench.Panel.ScrollByWheel(delta, workbench.Scale);
				Damage(workbench.PanelRect(device.Width, device.Height));
				return;
			}

			if (workbench.ShowSidebar && x >= workbench.ActivityBarWidth && x < workbench.EditorLeft) {
				if (workbench.Sidebar.View == ActivityView.SourceControl) {
					workbench.Sidebar.SourceControl.ScrollByWheel(delta, workbench.Scale);
				}
				else if (workbench.Sidebar.View == ActivityView.Run) {
					workbench.Sidebar.Debug.ScrollByWheel(delta, workbench.Scale);
				}
				else workbench.Sidebar.ScrollByWheel(delta, workbench.Scale);

				Damage(workbench.SidebarRect(device.Height));
				return;
			}

			if (Win32.IsKeyDown(Win32.VkShift)) {
				workbench.ActiveView?.ScrollByHorizontalWheel(-delta, painter.GetCode().GetCellWidth());
			}
			else {
				workbench.ActiveView?.ScrollByWheel(delta, painter.GetCode().GetLineHeight(),
					workbench.EditorHeight(device.Height));
			}

			RefreshEditor();
		};

		window.Events.MouseHorizontalWheel += (delta, _, _) => {
			workbench.ActiveView?.ScrollByHorizontalWheel(delta, painter.GetCode().GetCellWidth());
			RefreshEditor();
		};

		nint arrowCursor = AppWindow.SystemCursor(Win32.IdcArrow);
		nint beamCursor = AppWindow.SystemCursor(Win32.IdcIBeam);
		nint resizeWidthCursor = AppWindow.SystemCursor(Win32.IdcSizeWestEast);
		nint resizeHeightCursor = AppWindow.SystemCursor(Win32.IdcSizeNorthSouth);

		window.Cursor = arrowCursor;

		window.Events.MouseMove += (x, y) => {
			LastMouseX = x;
			LastMouseY = y;

			uiOwnsPointer = ui.InternalCursorMoved(x, y);

			if (uiOwnsPointer) return;

			if (workbench.ActiveView is { } marginView) {
				int gutterLine = x >= workbench.EditorLeft &&
					x < workbench.EditorLeft + TextView.GlyphMarginWidth && y >= workbench.EditorTop
						? marginView.Snapshot.GetLineAt(marginView.OffsetFromPoint(painter.GetCode(), x, y,
							workbench.EditorLeft, workbench.EditorTop))
						: -1;

				if (gutterLine != marginView.HoverGutterLine) {
					marginView.HoverGutterLine = gutterLine;

					Damage(new PixelRect((int)workbench.EditorLeft, (int)workbench.EditorTop,
						(int)(workbench.EditorLeft + TextView.GlyphMarginWidth), device.Height));
				}
			}

			if (workbench.AuxiliaryWidth > 0f) {
				bool overClose = workbench.Auxiliary.IsOverClose(x, y);

				if (overClose != workbench.Auxiliary.HoverClose) {
					workbench.Auxiliary.HoverClose = overClose;
					Damage(workbench.AuxiliaryRect(device.Width, device.Height));
				}
			}

			window.Cursor =
				workbench.IsOverSplitter(x, device.Height, y) ? resizeWidthCursor :
				workbench.IsOverAuxiliarySplitter(x, y, device.Width, device.Height) ? resizeWidthCursor :
				workbench.IsOverPanelSplitter(x, y, device.Height) ? resizeHeightCursor :
				workbench.ActiveView is not null &&
					workbench.IntelliSense?.Suggest.Contains(x, y) != true &&
					x >= workbench.EditorLeft + TextView.GlyphMarginWidth &&
					y >= workbench.EditorTop &&
					y < workbench.EditorBottom(device.Height)
					? beamCursor
					: arrowCursor;

			if (draggingSplitter) {
				workbench.ResizeSidebar(x);
				window.Invalidate();
				return;
			}

			if (draggingAuxiliary) {
				workbench.ResizeAuxiliary(x, device.Width);
				window.Invalidate();
				return;
			}

			if (draggingPanel) {
				workbench.ResizePanel(y, device.Height);
				window.Invalidate();
				return;
			}

			if (selectingOutput) {
				workbench.Panel.ExtendOutputSelection(x, y);
				Damage(workbench.PanelRect(device.Width, device.Height));
				return;
			}

			if (workbench.Panel.IsChannelMenuOpen) {
				workbench.Panel.SetChannelHover(workbench.Panel.ChannelAt(x, y, workbench.Scale));
				Damage(workbench.PanelRect(device.Width, device.Height));
				return;
			}

			if (workbench.Panel.IsProfileMenuOpen) {
				workbench.Panel.SetProfileHover(workbench.Panel.ProfileAt(painter, x, y, workbench.Scale));
				Damage(workbench.PanelRect(device.Width, device.Height));
				return;
			}

			if (workbench.Panel.IsOpen && y >= workbench.PanelTop(device.Height)) {
				workbench.Panel.SetHover(workbench.Panel.ViewAt(painter, x, y, workbench.Scale),
					workbench.Panel.ActionAt(x, y, workbench.Scale));

				workbench.Panel.SetTerminalHover(workbench.Panel.TerminalAt(x, y, workbench.Scale),
					workbench.Panel.IsOverTerminalClose(x, y, workbench.Scale));

				workbench.Panel.SetReferenceHover(workbench.Panel.ReferenceAt(y, workbench.Scale));

				Damage(workbench.PanelRect(device.Width, device.Height));
				return;
			}

			workbench.Panel.SetHover(-1, PanelPart.PanelAction.None);
			workbench.Panel.SetTerminalHover(-1, false);

			if (!selecting && workbench.Find.IsOpen) {
				workbench.Find.SetHover(workbench.Find.ButtonAt(x, y, workbench.Scale));
			}

			if (y < workbench.ShellTop) {
				HoverTitleBar(x, y);
				return;
			}

			if (workbench.TitleBar.Hover != TitleBarPart.None) {
				workbench.TitleBar.SetHover(TitleBarPart.None);
				Damage(workbench.TitleBarRect(device.Width));
			}

			float statusTop = workbench.StatusBarTop(device.Height);

			if (y >= statusTop) {
				DamageCleared(ClearChromeHover(workbench));

				if (workbench.StatusBar.SetHover(
					workbench.StatusBar.IndexAt(x, y, statusTop, workbench.Scale))) {
					Damage(workbench.StatusBarRect(device.Width, device.Height));
				}

				return;
			}

			if (workbench.StatusBar.SetHover(-1)) Damage(workbench.StatusBarRect(device.Width, device.Height));

			if (workbench.Sidebar.SourceControl.Menu.IsOpen) {
				if (workbench.Sidebar.SourceControl.Menu.Hover(x, y)) {
					Damage(workbench.Sidebar.SourceControl.Menu.Bounds);
				}

				return;
			}

			if (x < workbench.ActivityBarWidth) {
				DamageCleared(ClearChromeHover(workbench));

				if (workbench.ActivityBar.SetHover(workbench.ActivityBar.IndexAt(x, y,
					workbench.ShellTop, statusTop - workbench.ShellTop, workbench.Scale))) {
					Damage(workbench.ActivityBarRect(device.Height));
				}

				return;
			}

			if (workbench.ActivityBar.SetHover(-1)) Damage(workbench.ActivityBarRect(device.Height));

			if (workbench.Auxiliary.SetStripHover(workbench.Auxiliary.StripIndexAt(x, y, workbench.AuxiliaryStripLeft(device.Width), workbench.ShellTop, workbench.Scale))) Damage(PixelRect.Around(workbench.AuxiliaryStripLeft(device.Width), workbench.ShellTop, workbench.AuxiliaryStripWidth, statusTop - workbench.ShellTop));

			if (workbench.ShowSidebar && x < workbench.EditorLeft && !selecting &&
				workbench.Sidebar.View == ActivityView.SourceControl) {
				SourceControlView source = workbench.Sidebar.SourceControl;

				if (source.SetHover(source.RowAt(y, workbench.Scale),
					source.HitTest(x, y, workbench.Scale, out _, out _))) Damage(workbench.SidebarRect(device.Height));

				return;
			}

			if (workbench.ShowSidebar && x < workbench.EditorLeft && !selecting) {
				bool tabCleared = workbench.Tabs.HoverIndex != -1;
				workbench.Tabs.HoverIndex = -1;

				if (workbench.Sidebar.View == ActivityView.Run) {
					if (workbench.Sidebar.Debug.SetHover(x, y,
						workbench.ShellTop + (Metrics.SidebarHeaderHeight * workbench.Scale),
						workbench.Scale)) Damage(workbench.SidebarRect(device.Height));
				}
				else if (workbench.Sidebar.View == ActivityView.Explorer &&
					workbench.Sidebar.SetHover(workbench.Sidebar.RowAt(x, y, workbench.ActivityBarWidth,
						workbench.ShellTop, workbench.Scale))) {
					Damage(workbench.SidebarRect(device.Height));
				}

				if (tabCleared) Damage(workbench.TabStripRect(device.Width));

				return;
			}

			if (workbench.Sidebar.SetHover(-1)) Damage(workbench.SidebarRect(device.Height));

			int previousTab = workbench.Tabs.HoverIndex;
			bool previousClose = workbench.Tabs.HoverClose;

			workbench.Tabs.HoverIndex = workbench.Tabs.IndexAt(workbench.Documents, painter, x, y,
				workbench.EditorLeft, workbench.TabsTop, workbench.Scale);
			workbench.Tabs.HoverClose = workbench.Tabs.HoverIndex >= 0 && TabStrip.IsOverClose(
				workbench.Documents, painter, x, workbench.EditorLeft, workbench.Scale, workbench.Tabs.HoverIndex);

			if (previousTab != workbench.Tabs.HoverIndex || previousClose != workbench.Tabs.HoverClose) {
				Damage(workbench.TabStripRect(device.Width));
			}

			bool changed = false;
			bool editorOnly = false;

			if (selecting && workbench.ActiveView is { } dragging) {
				int before = dragging.CaretOffset;

				dragging.MoveTo(
					dragging.OffsetFromPoint(painter.GetCode(), x, y, workbench.EditorLeft, workbench.EditorTop),
					extend: true);

				editorOnly = dragging.CaretOffset != before;
			}

			if (workbench.IntelliSense is { Suggest.IsOpen: true } listed) {
				int row = listed.Suggest.RowAt(x, y);

				changed |= row != listed.Suggest.HoverRow;
				listed.Suggest.SetHover(row);
			}

			if (workbench.IntelliSense is { } hovering) {
				if (selecting || workbench.ActiveView is not { } hovered ||
					y < workbench.EditorTop || y >= workbench.EditorBottom(device.Height) ||
					x < workbench.EditorLeft) hovering.PointerLeft();
				else {
					int offset = hovered.OffsetFromPoint(painter.GetCode(), x, y, workbench.EditorLeft,
						workbench.EditorTop);

					int hoverLine = hovered.Snapshot.GetLineAt(offset);

					changed |= hovering.Hover.IsOpen;

					hovering.PointerMoved(hoverLine + 1,
						offset - hovered.Snapshot.GetLineStartOffset(hoverLine) + 1, x, y);
				}
			}

			if (changed) Damage(workbench.EditorCardRect(device.Width, device.Height));
			else if (editorOnly) RefreshEditor();
		};

		window.Events.MouseMove += (x, y) => {
			if (uiOwnsPointer) return;

			if (!selecting) {
				SetAutoScrolling(false);
				return;
			}

			dragX = x;
			dragY = y;

			float bottom = workbench.StatusBarTop(device.Height);
			float right = workbench.EditorLeft + workbench.TextWidth(device.Width);

			SetAutoScrolling(
				Overshoot(y, workbench.EditorTop, bottom) != 0f ||
				Overshoot(x, workbench.EditorLeft, right) != 0f);

			RefreshEditor();
		};

		window.Events.MouseDown += (x, y) => {
			if (ui.InternalMousePressed(MouseButton.Left, x, y)) return;

			if (workbench.Sidebar.SourceControl.Menu.IsOpen) {
				if (!workbench.Sidebar.SourceControl.Menu.Contains(x, y)) workbench.Sidebar.SourceControl.Menu.Close();

				Refresh();
				return;
			}

			if (workbench.IntelliSense is { } suggesting && suggesting.Suggest.Contains(x, y)) {
				if (suggesting.Suggest.RowAt(x, y) is >= 0 and int row) {
					suggesting.Suggest.Select(row);
					suggesting.Accept();
				}

				Refresh();
				return;
			}

			workbench.IntelliSense?.Dismiss();

			if (workbench.Find.Contains(x, y, workbench.Scale)) {
				workbench.Find.Press(workbench.Find.ButtonAt(x, y, workbench.Scale), workbench.ActiveView);
				context.ScrollToMatch(true);
				Refresh();
				return;
			}

			if (y < workbench.ShellTop) {
				if (workbench.TitleBar.CommandAt(x, y, device.Width, workbench.Scale) is { } titleCommand) context.Run(titleCommand);

				return;
			}

			float statusTop = workbench.StatusBarTop(device.Height);
			if (y >= statusTop) {
				int segment = workbench.StatusBar.IndexAt(x, y, statusTop, workbench.Scale);

				if (workbench.StatusBar.CommandAt(segment) is { } statusCommand) context.Run(statusCommand);

				return;
			}

			if (x < workbench.ActivityBarWidth) {
				int item = workbench.ActivityBar.IndexAt(x, y, workbench.ShellTop, statusTop - workbench.ShellTop, workbench.Scale);

				if (ActivityBar.CommandAt(item) is { } activityCommand) {
					context.Run(activityCommand);
					return;
				}

				if (item >= 0 && !workbench.ActivityBar.Activate(item)) workbench.ShowSidebar = !workbench.ShowSidebar;
				else if (item >= 0) workbench.ShowSidebar = true;

				workbench.Sidebar.View = workbench.ActivityBar.Active;

				if (workbench.Sidebar.View == ActivityView.SourceControl) {
					workbench.Sidebar.SourceControl.SetRoot(workbench.Repository?.GetRootPath() ?? rootPath);
					SourceControlCommands.RunGit(workbench, Refresh, static () => true);
				}

				Refresh();
				return;
			}

			if (workbench.IsOverPanelSplitter(x, y, device.Height)) {
				draggingPanel = true;
				Win32.SetCapture(window.Handle);
				return;
			}

			if (workbench.IsOverSplitter(x, device.Height, y)) {
				draggingSplitter = true;
				Win32.SetCapture(window.Handle);
				return;
			}

			if (workbench.IsOverAuxiliarySplitter(x, y, device.Width, device.Height)) {
				draggingAuxiliary = true;
				Win32.SetCapture(window.Handle);
				return;
			}

			if (workbench.Auxiliary.ShownViewId is not null &&
				!workbench.Auxiliary.ContentBounds.Contains(new PixelRect(x, y, x + 1, y + 1))) {
				workbench.Webviews.ReleaseFocus();
			}

			if (workbench.AuxiliaryWidth > 0f && workbench.Auxiliary.IsOverClose(x, y)) {
				workbench.Auxiliary.IsOpen = false;
				workbench.Auxiliary.HoverClose = false;
				Refresh();
				return;
			}

			if (workbench.Auxiliary.StripIndexAt(x, y, workbench.AuxiliaryStripLeft(device.Width), workbench.ShellTop, workbench.Scale) is var strip && strip >= 0) {
				workbench.Auxiliary.ActivateStrip(strip);
				Refresh();
				return;
			}

			if (workbench.Panel.IsChannelMenuOpen) {
				int channel = workbench.Panel.ChannelAt(x, y, workbench.Scale);

				if (channel >= 0) {
					workbench.Panel.ActivateChannel(channel);
					workbench.PanelFocused = true;
				}
				else workbench.Panel.CloseChannelMenu();

				Refresh();
				return;
			}

			if (workbench.Panel.IsProfileMenuOpen) {
				int profile = workbench.Panel.ProfileAt(painter, x, y, workbench.Scale);

				if (profile >= 0) {
					workbench.Panel.NewTerminal(workbench.Panel.Profiles[profile]);
					workbench.PanelFocused = true;
				}
				else workbench.Panel.CloseProfileMenu();

				Refresh();
				return;
			}

			if (workbench.Panel.IsOpen && y >= workbench.PanelTop(device.Height) && x >= workbench.EditorLeft) {
				workbench.PanelFocused = true;

				switch (workbench.Panel.ActionAt(x, y, workbench.Scale)) {
					case PanelPart.PanelAction.Close:
						workbench.Panel.IsOpen = false;
						workbench.PanelFocused = false;
						break;

					case PanelPart.PanelAction.ProfileMenu:
						workbench.Panel.ToggleProfileMenu();
						break;

					case PanelPart.PanelAction.NewTerminal:
						workbench.Panel.NewTerminal(TerminalProfiles.Default(
							workbench.Panel.Profiles, workbench.Panel.DefaultProfile));
						break;

					case PanelPart.PanelAction.ChannelMenu:
						workbench.Panel.ToggleChannelMenu();
						break;

					case PanelPart.PanelAction.ClearOutput:
						workbench.Panel.ActiveChannel.Clear();
						workbench.Panel.ClearOutputSelection();
						break;

					default:
						int panelView = workbench.Panel.ViewAt(painter, x, y, workbench.Scale);

						if (panelView >= 0) workbench.Panel.Activate(panelView);
						else if (workbench.Panel.Reference(
							workbench.Panel.ReferenceAt(y, workbench.Scale)) is { } location) {
							workbench.GoTo(location.Path, location.Line, location.Column);
						}
						else if (workbench.Panel.BeginOutputSelection(x, y)) selectingOutput = true;
						else if (workbench.Panel.TerminalAt(x, y, workbench.Scale) is int terminal and >= 0) {
							if (workbench.Panel.IsOverTerminalClose(x, y, workbench.Scale)) {
								workbench.Panel.CloseTerminal(terminal);
							}
							else workbench.Panel.ActivateTerminal(terminal);
						}

						break;
				}

				Refresh();
				return;
			}

			workbench.PanelFocused = false;

			if (workbench.ShowSidebar && x < workbench.EditorLeft) {
				if (workbench.Sidebar.View == ActivityView.SourceControl) {
					SourceControlView pressing = workbench.Sidebar.SourceControl;

					pressing.SetPressed(
						pressing.HitTest(x, y, workbench.Scale, out _, out _));

					pressing.FocusMessage(pressing.Pressed == SourceControlHit.Message);

					Refresh();
					return;
				}

				if (workbench.Sidebar.View == ActivityView.Run) {
					workbench.Sidebar.Debug.Click(y,
						workbench.ShellTop + (Metrics.SidebarHeaderHeight * workbench.Scale), workbench.Scale);

					Refresh();
					return;
				}

				if (workbench.Sidebar.View != ActivityView.Explorer) {
					Refresh();
					return;
				}

				int row = workbench.Sidebar.RowAt(x, y, workbench.ActivityBarWidth, workbench.ShellTop, workbench.Scale);
				if (workbench.Sidebar.Activate(row) is { } path) workbench.OpenFile(path);

				Refresh();
				return;
			}

			if (y < workbench.EditorTop && y >= workbench.ShellTop) {
				int tab = workbench.Tabs.IndexAt(workbench.Documents, painter, x, y, workbench.EditorLeft,
					workbench.TabsTop, workbench.Scale);
				if (tab >= 0) {
					if (TabStrip.IsOverClose(workbench.Documents, painter, x, workbench.EditorLeft,
						workbench.Scale, tab)) workbench.Close(tab);
					else workbench.Activate(tab);
				}

				Refresh();
				return;
			}

			if (workbench.ActiveView is { } view) {
				selecting = true;

				Win32.SetCapture(window.Handle);

				long now = Environment.TickCount64;

				clickCount = now - lastClickAt <= Win32.DoubleClickTime &&
					Math.Abs(x - lastClickX) <= 4 && Math.Abs(y - lastClickY) <= 4
					? clickCount + 1
					: 1;

				lastClickAt = now;
				lastClickX = x;
				lastClickY = y;

				int offset = view.OffsetFromPoint(painter.GetCode(), x, y, workbench.EditorLeft,
					workbench.EditorTop);

				float gutterWidth = view.GutterWidth(painter.GetCode());
				bool inGutter = x < workbench.EditorLeft + gutterWidth;
				bool inBreakpointStrip = x < workbench.EditorLeft + TextView.GlyphMarginWidth;

				if (inBreakpointStrip && workbench.ActiveFilePath is { Length: > 0 } breakpointPath) {
					debugger.ToggleBreakpoint(breakpointPath, view.Snapshot.GetLineAt(offset) + 1);
					Refresh();
					return;
				}

				if (inGutter || clickCount >= 3) view.SelectLineAt(offset);
				else if (clickCount == 2) view.SelectWordAt(offset);
				else view.MoveTo(offset, extend: Win32.IsKeyDown(Win32.VkShift));
			}

			Refresh();
		};

		window.Events.MouseUp += (x, y) => {
			if (ui.InternalMouseReleased(MouseButton.Left, x, y)) return;

			if (workbench.Sidebar.SourceControl.Menu is { IsOpen: true } gitMenu && gitMenu.Contains(x, y)) {
				if (gitMenu.CommandAt(x, y) is var command && command != GitCommand.None) {
					gitMenu.Close();
					SourceControlCommands.InvokeGit(workbench, command, Refresh);
				}

				Refresh();
				return;
			}

			if (workbench.Sidebar.SourceControl.Pressed != SourceControlHit.None) {
				SourceControlCommands.HandleSourceControlClick(workbench, x, y, Refresh);
				workbench.Sidebar.SourceControl.SetPressed(SourceControlHit.None);
				Refresh();
				return;
			}

			if (selecting || draggingSplitter || draggingAuxiliary || draggingPanel || selectingOutput) {
				Win32.ReleaseCapture();
				window.Invalidate();
			}

			selecting = false;
			draggingSplitter = false;
			draggingAuxiliary = false;
			draggingPanel = false;
			selectingOutput = false;
			SetAutoScrolling(false);
		};

		window.Events.CharacterTyped += character => {
			if (character < ' ' || character == 0x7F || Win32.IsKeyDown(Win32.VkControl)) return;

			if (ui.InternalKeyTyped(character)) return;

			context.Focused?.Type?.Invoke(character);
		};

		window.Events.KeyDown += key => {
			bool shift = Win32.IsKeyDown(Win32.VkShift);
			bool control = Win32.IsKeyDown(Win32.VkControl);
			bool alt = Win32.IsKeyDown(Win32.VkMenu);

			if (ui.InternalKeyCodePressed(key, Modifiers())) return;

			if (context.EditorFocused && workbench.IntelliSense is { } intelliSense && intelliSense.HandleKey(key, control)) return;

			if (Registry.Resolve(key, control, shift, alt) is { } command) {
				workbench.RunCommand(command.GetId(), context.Host);
				return;
			}

			if (context.Focused is not { } target) return;

			if (key == VirtualKey.Back && !control && target.DeleteLeft is { } deleteLeft) {
				deleteLeft();
				return;
			}

			target.Key?.Invoke(key, control);
		};
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

	[System.Runtime.CompilerServices.InlineArray(8)]
	private struct PopupRects
	{
		private PixelRect _first;
	}

	private static float Overshoot(float value, float low, float high) {
		const float maximum = 160f;

		if (value < low) return -Math.Min(low - value, maximum);

		return value > high ? Math.Min(value - high, maximum) : 0f;
	}

	private static ChromeRegions ClearChromeHover(Workbench workbench) {
		bool sidebar = workbench.Sidebar.SetHover(-1);
		bool activity = workbench.ActivityBar.SetHover(-1);
		bool tabs = workbench.Tabs.HoverIndex != -1 || workbench.Tabs.HoverClose;

		workbench.Tabs.HoverIndex = -1;
		workbench.Tabs.HoverClose = false;

		return new ChromeRegions(sidebar, activity, tabs);
	}

	private readonly record struct ChromeRegions(bool Sidebar, bool ActivityBar, bool Tabs);

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
