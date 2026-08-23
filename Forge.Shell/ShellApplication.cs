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
using Forge.Terminal;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.UI;
using Forge.Workspace;
using Forge.VSCode;

namespace Forge.Shell;

[ForgeComponent]
public sealed class ShellApplication(ForgeBuilder builder) : IForgeApplication
{
	public static void Link(IServiceCollection services) => services.AddSingleton<IForgeApplication, ShellApplication>();


	#region Virtual keys

	private const uint VkBack = 0x08;
	private const uint VkReturn = 0x0D;
	private const uint VkEscape = 0x1B;
	private const uint VkPageUp = 0x21;
	private const uint VkPageDown = 0x22;
	private const uint VkEnd = 0x23;
	private const uint VkHome = 0x24;
	private const uint VkLeft = 0x25;
	private const uint VkUp = 0x26;
	private const uint VkRight = 0x27;
	private const uint VkDown = 0x28;
	private const uint VkDelete = 0x2E;
	private const uint Vk0 = 0x30;
	private const uint VkNumpad0 = 0x60;
	private const uint VkPlus = 0x6B;
	private const uint VkMinus = 0x6D;
	private const uint VkOemPlus = 0xBB;
	private const uint VkOemMinus = 0xBD;
	private const uint VkA = 0x41;
	private const uint VkB = 0x42;
	private const uint VkC = 0x43;
	private const uint VkF = 0x46;
	private const uint VkJ = 0x4A;
	private const uint VkM = 0x4D;
	private const uint VkP = 0x50;
	private const uint VkU = 0x55;
	private const uint VkS = 0x53;
	private const uint VkV = 0x56;
	private const uint VkW = 0x57;
	private const uint VkX = 0x58;
	private const uint VkY = 0x59;
	private const uint VkZ = 0x5A;
	private const uint VkTab = 0x09;
	private const uint VkK = 0x4B;
	private const uint VkL = 0x4C;
	private const uint VkOemSlash = 0xBF;

	private const uint VkD = 0x44;

	private const uint VkOemBackslash = 0xDC;

	private const uint VkOemOpenBrackets = 0xDB;
	private const uint VkOemCloseBrackets = 0xDD;

	private const uint VkF5 = 0x74;
	private const uint VkF6 = 0x75;
	private const uint VkF9 = 0x78;
	private const uint VkF10 = 0x79;
	private const uint VkF11 = 0x7A;
	private const uint VkF12 = 0x7B;

	private const uint VkOemTilde = 0xC0;

	#endregion

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
		ambient.SetAccent(Theme.Accent);

		using FileIconSet fileIcons = new(device.Factory,
			Path.Combine(AppContext.BaseDirectory, "Assets"));

		painter.FileIcons = fileIcons;

		using Image appIcon = new(device,
			Path.Combine(AppContext.BaseDirectory, "Assets", "forge-icon.bgra"));

		using Workbench workbench = new(scale) { ShowMinimap = settings.MinimapEnabled };
		workbench.TitleBar.Icon = appIcon;

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
				ambient.Pulse(AmbientSignal.Git, 0.85f);
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
				ambient.End(AmbientSignal.Build);
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

		debugger.OnStateChanged(() => {
			if (debugger.IsActive()) ambient.Begin(AmbientSignal.Debug);
			else ambient.End(AmbientSignal.Debug);

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

		DebugCommands.Register(workbench.Commands, debugger,
			(id, title, category, handler) => new Forge.Commands.Command(id, title, category, handler));

		ThreadPool.UnsafeQueueUserWorkItem(_ => LoadExtensions(workbench, window.Invalidate, Post), null);

		IFileIndex index = new FileIndex(rootPath);
		workbench.QuickOpen.Attach(index);

		index.OnUpdated(() => {
			ambient.Pulse(AmbientSignal.Search, 0.55f);
			workbench.QuickOpen.OnIndexUpdated();

			if (workbench.QuickOpen.IsOpen) window.Invalidate(workbench.QuickOpen.Bounds);
		});

		fileIcons.OnReady(window.Invalidate);

		window.Events.PlacementChanged += () => window.CaptureState().Save(UserData.WindowStatePath);

		index.StartScan();

		workbench.ActiveDocumentChanged += () => ApplyLanguageColor(ambient, fileIcons, workbench.ActiveFilePath);

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
		string folder = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar));
		Document? document = workbench.ActiveDocument;

		string title = document is null
			? $"Forge - {folder}"
			: $"{(document.IsDirty() ? "● " : string.Empty)}{document.Name} - {folder} - Forge";

		workbench.TitleBar.Title = "Forge";
		workbench.TitleBar.DocumentName = document is null
			? null
			: $"{(document.IsDirty() ? "● " : string.Empty)}{document.Name}";

		workbench.TitleBar.FolderName = folder;
		window.SetTitle(title);
	}

	private static void ApplyLanguageColor(IAmbient ambient, IFileIconSet icons, string? path) {
		if (string.IsNullOrEmpty(path) || icons.IconForFile(Path.GetFileName(path)) is not { } icon) {
			ambient.SetLanguageColor(default, 0f);
			return;
		}

		Color dominant = icon.GetDominantColor();

		ambient.SetLanguageColor(dominant, dominant.A > 0.05f ? 1f : 0f);
	}

	private static string? DebugKeyBinding(uint key, bool shift, bool control, DebugState state) => key switch {
		VkF5 when control && shift => DebugCommands.Restart,
		VkF5 when shift => DebugCommands.Stop,
		VkF5 when control => DebugCommands.Run,
		VkF5 => state == DebugState.Stopped ? DebugCommands.Continue : DebugCommands.Start,
		VkF6 => DebugCommands.Pause,
		VkF9 => DebugCommands.ToggleBreakpoint,
		VkF10 => DebugCommands.StepOver,
		VkF11 when shift => DebugCommands.StepOut,
		VkF11 => DebugCommands.StepInto,
		_ => null,
	};

	private static void WireEvents(AppWindow window, RenderDevice device, IPainter painter, FontSet fonts,
		Settings settings, Workbench workbench, Ambient ambient, DebugService debugger,
		System.Collections.Concurrent.ConcurrentQueue<Action> uiWork, string rootPath) {
		bool selecting = false;
		bool draggingSplitter = false;
		bool draggingAuxiliary = false;
		bool draggingPanel = false;
		bool selectingOutput = false;

		bool panelFocused = false;

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

		void Damage(PixelRect rect) {
			pending.Add(rect);
			window.Invalidate(rect);
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
			workbench.Webviews.Show(entry.ViewId, html, entry.Directory, bounds);
		}

		void AmbientTick() {
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			float delta = (float)((now - ambientStamp) / (double)System.Diagnostics.Stopwatch.Frequency);
			ambientStamp = now;

			SyncWebviews();

			while (uiWork.TryDequeue(out Action? work)) {
				try {
					work();
				}
				catch (Exception exception) when (exception is InvalidOperationException or IOException) {
					workbench.Panel.Output.Append($"Debug UI update failed: {exception.Message}");
				}
			}

			ambient.SetFocused(window.IsForeground());

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

			if (id != caretTimerId || workbench.QuickOpen.IsOpen) return;

			Caret.Visible = !Caret.Visible;

			if (workbench.Sidebar.View == ActivityView.SourceControl &&
				workbench.Sidebar.SourceControl.MessageFocused) Damage(workbench.SidebarRect(device.Height));

			if (workbench.ActiveView is not { } blinking) return;

			blinking.CaretVisible = !blinking.CaretVisible;

			Damage(blinking.CaretRect(painter.GetCode(), workbench.EditorLeft, workbench.EditorTop,
				workbench.TextWidth(device.Width), workbench.EditorHeight(device.Height)));
		};

		void ScrollToMatch(bool found) {
			if (found && workbench.ActiveView is { } matched) {
				matched.EnsureCaretVisible(painter.GetCode(), workbench.TextWidth(device.Width),
					workbench.EditorHeight(device.Height));
			}
		}

		void Invoke(MenuCommand command) {
			workbench.Menu.Close();
			workbench.TitleBar.OpenMenuIndex = -1;

			TextView? view = workbench.ActiveView;

			switch (command) {
				case MenuCommand.RunCommand when workbench.Menu.CommandId is { } id:
					workbench.RunCommand(id, new CommandHost(workbench, Refresh));
					break;

				case MenuCommand.OpenFile when FileDialog.OpenFile(window.Handle) is { } path:
					workbench.OpenFile(path);
					break;

				case MenuCommand.OpenFolder when FileDialog.OpenFolder(window.Handle) is { } folder:
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, folder) {
						UseShellExecute = true,
					});
					break;

				case MenuCommand.Save:
					workbench.SaveActive();
					break;

				case MenuCommand.SaveAs when workbench.ActiveDocument is { } document:
					if (FileDialog.SaveFile(window.Handle, document.Name) is { } target) document.SaveAs(target);
					break;

				case MenuCommand.CloseEditor:
					workbench.CloseActive();
					break;

				case MenuCommand.OpenSettings:
					workbench.OpenFile(UserData.SettingsPath);
					break;

				case MenuCommand.Exit:
					window.CloseWindow();
					return;

				case MenuCommand.Undo:
					view?.Undo();
					break;

				case MenuCommand.Redo:
					view?.Redo();
					break;

				case MenuCommand.Cut when view is not null:
					Clipboard.SetText(view.GetSelectedText());
					view.DeleteSelection();
					break;

				case MenuCommand.Copy when view is not null:
					Clipboard.SetText(view.GetSelectedText());
					break;

				case MenuCommand.Paste when view is not null:
					view.InsertText(Clipboard.GetText());
					break;

				case MenuCommand.SelectAll:
					view?.SelectAll();
					break;

				case MenuCommand.ToggleSidebar:
					workbench.ShowSidebar = !workbench.ShowSidebar;
					break;

				case MenuCommand.ToggleAuxiliaryBar:
					workbench.Auxiliary.Toggle();
					break;

				case MenuCommand.TogglePanel:
					workbench.Panel.IsOpen = !workbench.Panel.IsOpen;
					break;

				case MenuCommand.ShowTerminal:
					workbench.Panel.Show(PanelView.Terminal);
					panelFocused = true;
					break;

				case MenuCommand.ShowProblems:
					workbench.Panel.Show(PanelView.Problems);
					break;

				case MenuCommand.ShowOutput:
					workbench.Panel.Show(PanelView.Output);
					break;

				case MenuCommand.ToggleMinimap:
					workbench.ShowMinimap = !workbench.ShowMinimap;
					settings.SetMinimapEnabled(workbench.ShowMinimap);
					break;

				case MenuCommand.ToggleFrameStats:
					workbench.ShowFrameStats = !workbench.ShowFrameStats;
					break;

				case MenuCommand.ShowCommands:
					workbench.Palette.Show(workbench.Commands);
					break;

				case MenuCommand.ZoomIn:
					settings.AdjustZoom(1f);
					ApplyZoom();
					return;

				case MenuCommand.ZoomOut:
					settings.AdjustZoom(-1f);
					ApplyZoom();
					return;

				case MenuCommand.ZoomReset:
					settings.ResetZoom();
					ApplyZoom();
					return;

				case MenuCommand.GoToFile:
					workbench.QuickOpen.Show();
					break;
			}

			Refresh();
		}

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

			ambient.Prepare(device);

			device.BeginDraw();

			for (int pass = 0; pass < frameDamage.Count; pass++) {
				PixelRect repaint = frameDamage[pass].Intersect(surface);

				if (repaint.IsEmpty) continue;

				painter.BeginFrame(repaint);
				painter.PushClip(repaint.Left, repaint.Top, repaint.Width, repaint.Height);

				device.Clear(Theme.EditorBackground);

				try {
					workbench.Render(painter, device.Width, device.Height);
					ambient.Composite(device);
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
			int menu = part == TitleBarPart.Menu
				? bar.MenuIndexAt(painter, x, y, workbench.Scale)
				: -1;

			if (part == bar.Hover && menu == bar.HoverMenuIndex) return;

			bar.SetHover(part, menu);
			Damage(workbench.TitleBarRect(device.Width));
		}

		window.Events.TitleBarMouseMove += HoverTitleBar;

		window.Events.TitleBarMouseLeave += () => {
			if (workbench.TitleBar.Hover == TitleBarPart.None) return;

			workbench.TitleBar.SetHover(TitleBarPart.None);
			Damage(workbench.TitleBarRect(device.Width));
		};

		window.Events.MouseWheel += (delta, x, _) => {
			if (Win32.IsKeyDown(Win32.VkControl)) {
				settings.AdjustZoom(delta > 0 ? 0.2f : -0.2f);
				ApplyZoom();
				return;
			}

			if (workbench.Palette.IsOpen) {
				if (workbench.Palette.ScrollByWheel(delta)) Damage(workbench.Palette.Bounds);

				return;
			}

			if (workbench.QuickOpen.IsOpen) return;

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

			ambient.SetPointer(x / MathF.Max(device.Width, 1f), y / MathF.Max(device.Height, 1f));

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
				workbench.ActiveView is not null && !workbench.Palette.IsOpen &&
					!workbench.QuickOpen.IsOpen &&
					workbench.IntelliSense?.Suggest.Contains(x, y) != true &&
					x >= workbench.EditorLeft + TextView.GlyphMarginWidth &&
					y >= workbench.EditorTop &&
					y < workbench.EditorBottom(device.Height)
					? beamCursor
					: arrowCursor;

			if (workbench.Palette.IsOpen) {
				if (workbench.Palette.SetHover(workbench.Palette.RowAt(x, y))) Damage(workbench.Palette.Bounds);

				return;
			}

			if (workbench.QuickOpen.IsOpen) {
				if (workbench.QuickOpen.SetHover(workbench.QuickOpen.RowAt(x, y))) Damage(workbench.QuickOpen.Bounds);

				return;
			}

			if (workbench.Menu.IsOpen) {
				if (y < workbench.ShellTop) {
					HoverTitleBar(x, y);

					int over = workbench.TitleBar.MenuIndexAt(painter, x, y, workbench.Scale);

					if (over >= 0 && over != workbench.Menu.MenuIndex) {
						workbench.Menu.Open(over);
						workbench.TitleBar.OpenMenuIndex = over;
						window.Invalidate();
					}

					return;
				}

				if (workbench.Menu.Hover(x, y)) Damage(workbench.Menu.Bounds);

				return;
			}

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

			workbench.Panel.SetHover(-1, Panel.PanelAction.None);
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
			if (workbench.LaunchPicker.IsOpen && !workbench.LaunchPicker.Contains(x, y) &&
				workbench.TitleBar.PartAt(x, y, device.Width, workbench.Scale)
					!= TitleBarPart.DebugConfiguration) {
				workbench.LaunchPicker.Close();
				Refresh();
				return;
			}

			if (workbench.Palette.IsOpen) {
				int row = workbench.Palette.RowAt(x, y);

				if (row >= 0 && workbench.Palette.Select(row)) {
					ICommand? picked = workbench.Palette.Accept();
					workbench.Palette.Hide();

					if (picked is { } command) workbench.RunCommand(command.GetId(), new CommandHost(workbench, Refresh));
				}
				else if (!workbench.Palette.Contains(x, y)) workbench.Palette.Hide();
				else return;

				Refresh();
				return;
			}

			if (workbench.QuickOpen.IsOpen) {
				int row = workbench.QuickOpen.RowAt(x, y);

				if (row >= 0 && workbench.QuickOpen.Select(row)) {
					if (workbench.QuickOpen.Accept() is { } path) workbench.OpenFile(path);

					workbench.QuickOpen.Hide();
				}
				else if (!workbench.QuickOpen.Contains(x, y)) workbench.QuickOpen.Hide();
				else return;

				Refresh();
				return;
			}

			if (workbench.Menu.IsOpen) {
				if (workbench.Menu.Contains(x, y)) return;

				int clicked = y < workbench.ShellTop &&
					workbench.TitleBar.PartAt(x, y, device.Width, workbench.Scale) == TitleBarPart.Menu
					? workbench.TitleBar.MenuIndexAt(painter, x, y, workbench.Scale)
					: -1;

				bool reopen = clicked >= 0 && clicked != workbench.Menu.MenuIndex;

				workbench.Menu.Open(reopen ? clicked : -1);
				workbench.TitleBar.OpenMenuIndex = workbench.Menu.MenuIndex;
				Refresh();
				return;
			}

			if (workbench.Sidebar.SourceControl.Menu.IsOpen) {
				if (!workbench.Sidebar.SourceControl.Menu.Contains(x, y)) workbench.Sidebar.SourceControl.Menu.Close();

				Refresh();
				return;
			}

			if (workbench.Palette.IsOpen) {
				workbench.Palette.Hide();
				Refresh();
				return;
			}

			if (workbench.QuickOpen.IsOpen) {
				workbench.QuickOpen.Hide();
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
				ScrollToMatch(true);
				Refresh();
				return;
			}

			if (y < workbench.ShellTop) {
				switch (workbench.TitleBar.PartAt(x, y, device.Width, workbench.Scale)) {
					case TitleBarPart.Menu:
						workbench.Menu.Open(workbench.TitleBar.MenuIndexAt(painter, x, y, workbench.Scale));
						workbench.TitleBar.OpenMenuIndex = workbench.Menu.MenuIndex;
						break;

					case TitleBarPart.CommandCentre:
						workbench.QuickOpen.Show();
						break;

					case TitleBarPart.Layout:
						workbench.ShowSidebar = !workbench.ShowSidebar;
						break;

					case TitleBarPart.Settings:
						workbench.OpenFile(UserData.SettingsPath);
						break;

					case TitleBarPart.DebugStart:
						if (debugger.IsActive()) debugger.StopSession(disconnect: false, suspend: false);
						else debugger.StartDebugging(null, noDebug: false);

						break;

					case TitleBarPart.DebugContinue:
						if (debugger.GetState() == DebugState.Stopped) debugger.Continue();
						else debugger.Pause();

						break;

					case TitleBarPart.DebugStepOver:
						debugger.StepOver();
						break;

					case TitleBarPart.DebugStepInto:
						debugger.StepInto();
						break;

					case TitleBarPart.DebugStepOut:
						debugger.StepOut();
						break;

					case TitleBarPart.DebugRestart:
						debugger.RestartSession();
						break;

					case TitleBarPart.DebugStop:
						debugger.StopSession(disconnect: false, suspend: false);
						break;

					case TitleBarPart.DebugConfiguration: {
						List<string> names = [];

						foreach (DebugConfiguration configuration in debugger.GetConfigurations()) {
							names.Add(configuration.Name);
						}

						workbench.LaunchPicker.Selected = debugger.GetSelectedConfiguration();

						workbench.LaunchPicker.Open(names, workbench.TitleBar.DebugControlLeft,
							TitleBar.Height(workbench.Scale));

						break;
					}
				}

				Refresh();
				return;
			}

			float statusTop = workbench.StatusBarTop(device.Height);
			if (y >= statusTop) {
				int segment = workbench.StatusBar.IndexAt(x, y, statusTop, workbench.Scale);

				if (workbench.StatusBar.CommandAt(segment) is { } statusCommand) {
					if (statusCommand == "workbench.panel.problems") workbench.Panel.Show(PanelView.Problems);
					else workbench.RunCommand(statusCommand, new CommandHost(workbench, Refresh));

					Refresh();
				}

				return;
			}

			if (x < workbench.ActivityBarWidth) {
				int item = workbench.ActivityBar.IndexAt(x, y, workbench.ShellTop, statusTop - workbench.ShellTop, workbench.Scale);

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

			if (workbench.AuxiliaryWidth > 0f && workbench.Auxiliary.TabAt(x, y,
				workbench.AuxiliaryLeft(device.Width), workbench.ShellTop, workbench.AuxiliaryWidth,
				workbench.Scale) is int auxiliaryTab && auxiliaryTab >= 0) {
				if (workbench.Auxiliary.Select(workbench.Auxiliary.Views[auxiliaryTab].ViewId)) {
					workbench.Auxiliary.ShownViewId = null;
					window.Invalidate();
				}

				return;
			}

			if (workbench.Panel.IsChannelMenuOpen) {
				int channel = workbench.Panel.ChannelAt(x, y, workbench.Scale);

				if (channel >= 0) {
					workbench.Panel.ActivateChannel(channel);
					panelFocused = true;
				}
				else workbench.Panel.CloseChannelMenu();

				Refresh();
				return;
			}

			if (workbench.Panel.IsProfileMenuOpen) {
				int profile = workbench.Panel.ProfileAt(painter, x, y, workbench.Scale);

				if (profile >= 0) {
					workbench.Panel.NewTerminal(workbench.Panel.Profiles[profile]);
					panelFocused = true;
				}
				else workbench.Panel.CloseProfileMenu();

				Refresh();
				return;
			}

			if (workbench.Panel.IsOpen && y >= workbench.PanelTop(device.Height) && x >= workbench.EditorLeft) {
				panelFocused = true;

				switch (workbench.Panel.ActionAt(x, y, workbench.Scale)) {
					case Panel.PanelAction.Close:
						workbench.Panel.IsOpen = false;
						panelFocused = false;
						break;

					case Panel.PanelAction.ProfileMenu:
						workbench.Panel.ToggleProfileMenu();
						break;

					case Panel.PanelAction.NewTerminal:
						workbench.Panel.NewTerminal(TerminalProfiles.Default(
							workbench.Panel.Profiles, workbench.Panel.DefaultProfile));
						break;

					case Panel.PanelAction.ChannelMenu:
						workbench.Panel.ToggleChannelMenu();
						break;

					case Panel.PanelAction.ClearOutput:
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

			panelFocused = false;

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
			if (workbench.LaunchPicker.IsOpen && workbench.LaunchPicker.Contains(x, y)) {
				if (workbench.LaunchPicker.Accept(x, y, workbench.Scale) is { } chosen) {
					debugger.SelectConfiguration(chosen);
				}

				workbench.LaunchPicker.Close();
				Refresh();
				return;
			}

			if (workbench.Menu.IsOpen && workbench.Menu.Contains(x, y)) {
				if (workbench.Menu.CommandAt(x, y) is var picked && picked != MenuCommand.None) Invoke(picked);

				return;
			}

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

			ambient.Pulse(AmbientSignal.Typing, 0.6f);

			if (workbench.Palette.IsOpen) {
				workbench.Palette.Append(character);
				Damage(workbench.Palette.Bounds);
				return;
			}

			if (workbench.QuickOpen.IsOpen) {
				workbench.QuickOpen.Append(character);
				Damage(workbench.QuickOpen.Bounds);
				return;
			}

			if (workbench.Sidebar.View == ActivityView.SourceControl &&
				workbench.Sidebar.SourceControl.MessageFocused) {
				workbench.Sidebar.SourceControl.Append(character);
				Refresh();
				return;
			}

			if (panelFocused && workbench.Panel.Active == PanelView.Terminal &&
				workbench.Panel.Terminal is { } shell) {
				shell.Write([character]);
				return;
			}

			if (workbench.Find.IsOpen) {
				workbench.Find.Append(character);
				ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
				Refresh();
				return;
			}

			if (workbench.ActiveView is { } view) {
				if (!view.TryAutoClose(character)) view.InsertText([character]);

				view.EnsureCaretVisible(painter.GetCode(), workbench.TextWidth(device.Width),
					workbench.EditorHeight(device.Height));

				workbench.IntelliSense?.Typed(character);

				RefreshEditor();
				return;
			}

			Refresh();
		};

		window.Events.KeyDown += key => {
			bool shift = Win32.IsKeyDown(Win32.VkShift);
			bool control = Win32.IsKeyDown(Win32.VkControl);
			bool alt = Win32.IsKeyDown(Win32.VkMenu);

			if (DebugKeyBinding(key, shift, control, debugger.GetState()) is { } debugCommand) {
				workbench.RunCommand(debugCommand, new CommandHost(workbench, Refresh));
				Refresh();
				return;
			}

			if (workbench.Palette.IsOpen) {
				switch (key) {
					case VkEscape:
						workbench.Palette.Hide();
						break;

					case VkUp:
						workbench.Palette.MoveSelection(-1);
						break;

					case VkDown:
						workbench.Palette.MoveSelection(1);
						break;

					case VkBack:
						workbench.Palette.Backspace();
						break;

					case VkReturn:
						if (workbench.Palette.Accept() is { } picked) {
							workbench.Palette.Hide();
							picked.Run(new CommandHost(workbench, Refresh));
						}

						break;

					default:
						return;
				}

				Refresh();
				return;
			}

			if (workbench.Sidebar.SourceControl.Menu.IsOpen && key == VkEscape) {
				workbench.Sidebar.SourceControl.Menu.Close();
				window.Invalidate();
				return;
			}

			if (workbench.Menu.IsOpen) {
				switch (key) {
					case VkEscape:
						workbench.Menu.Close();
						workbench.TitleBar.OpenMenuIndex = -1;
						break;

					case VkUp:
						workbench.Menu.MoveSelection(-1);
						break;

					case VkDown:
						workbench.Menu.MoveSelection(1);
						break;

					case VkLeft:
					case VkRight:
						workbench.Menu.Cycle(key == VkLeft ? -1 : 1);
						workbench.TitleBar.OpenMenuIndex = workbench.Menu.MenuIndex;
						break;

					case VkReturn:
						Invoke(workbench.Menu.Accept());
						return;
				}

				Refresh();
				return;
			}

			if (workbench.QuickOpen.IsOpen && HandleQuickOpenKey(workbench, key, window, Refresh)) return;

			if (workbench.Sidebar.View == ActivityView.SourceControl &&
				workbench.Sidebar.SourceControl.MessageFocused) {
				switch (key) {
					case VkEscape:
						workbench.Sidebar.SourceControl.FocusMessage(false);
						break;

					case VkBack:
						workbench.Sidebar.SourceControl.Backspace();
						break;

					case VkReturn when control:
						SourceControlCommands.HandleCommit(workbench, Refresh);
						break;

					case VkReturn:
						workbench.Sidebar.SourceControl.Append('\n');
						break;

					default:
						return;
				}

				Refresh();
				return;
			}

			if (workbench.Find.IsOpen) {
				switch (key) {
					case VkEscape:
						workbench.Find.Hide();
						break;

					case VkBack:
						workbench.Find.Backspace();
						ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
						break;

					case VkReturn:
						ScrollToMatch(workbench.Find.Step(workbench.ActiveView, shift ? -1 : 1));
						break;

					case VkUp:
						ScrollToMatch(workbench.Find.Step(workbench.ActiveView, -1));
						break;

					case VkDown:
						ScrollToMatch(workbench.Find.Step(workbench.ActiveView, 1));
						break;

					case VkC when control:
						workbench.Find.ToggleMatchCase();
						ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
						break;

					case VkF when control:
						workbench.Find.Show(workbench.ActiveView);
						ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
						break;

					default:
						return;
				}

				Refresh();
				return;
			}

			switch (key) {
				case VkOemTilde when control:
					workbench.Panel.Toggle(PanelView.Terminal);
					panelFocused = workbench.Panel.IsOpen;
					Refresh();
					return;

				case VkJ when control:
					workbench.Panel.IsOpen = !workbench.Panel.IsOpen;
					panelFocused = workbench.Panel.IsOpen;
					Refresh();
					return;

				case VkM when control && shift:
					workbench.Panel.Show(PanelView.Problems);
					panelFocused = false;
					Refresh();
					return;

				case VkU when control && shift:
					workbench.Panel.Show(PanelView.Output);
					panelFocused = false;
					Refresh();
					return;
			}

			if (panelFocused && workbench.Panel.Active == PanelView.Output && control) {
				if (key == VkC) {
					Clipboard.SetText(workbench.Panel.CopyOutput());
					return;
				}

				if (key == VkA) {
					workbench.Panel.SelectAllOutput();
					Refresh();
					return;
				}
			}

			if (panelFocused && workbench.Panel.Active == PanelView.Terminal &&
				workbench.Panel.Terminal is { } terminal) {
				if (key == VkEscape) {
					panelFocused = false;
					Refresh();
					return;
				}

				if (control && key is >= VkA and <= VkZ) {
					terminal.SendControl((char)key);
					Refresh();
					return;
				}

				if (key == VkRight && !control && terminal.AcceptSuggestion()) return;

				TerminalKey mapped = key switch {
					VkReturn => TerminalKey.Enter,
					VkBack => TerminalKey.Backspace,
					VkUp => TerminalKey.Up,
					VkDown => TerminalKey.Down,
					VkLeft => TerminalKey.Left,
					VkRight => TerminalKey.Right,
					VkHome => TerminalKey.Home,
					VkEnd => TerminalKey.End,
					VkDelete => TerminalKey.Delete,
					VkPageUp => TerminalKey.PageUp,
					VkPageDown => TerminalKey.PageDown,
					_ => TerminalKey.None,
				};

				if (terminal.SendKey(mapped, control)) return;
			}

			if (workbench.IntelliSense is { } intelliSense && intelliSense.HandleKey(key, control)) return;

			switch (key) {
				case VkP when control && shift:
					workbench.Palette.Show(workbench.Commands);
					Refresh();
					return;

				case VkP when control:
					workbench.QuickOpen.Show();
					Refresh();
					return;

				case VkF when control:
					workbench.Find.Show(workbench.ActiveView);
					ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
					Refresh();
					return;

				case VkB when control && Win32.IsKeyDown(Win32.VkMenu):
					workbench.Auxiliary.Toggle();
					Refresh();
					return;

				case VkB when control:
					workbench.ShowSidebar = !workbench.ShowSidebar;
					Refresh();
					return;

				case VkS when control:
					workbench.SaveActive();
					Refresh();
					return;

				case VkW when control:
					workbench.CloseActive();
					Refresh();
					return;

				case VkF12 when control && shift:
					workbench.ShowFrameStats = !workbench.ShowFrameStats;
					Refresh();
					return;

				case VkF12 when shift:
					workbench.RunCommand("dotnet.findAllReferences",
						new CommandHost(workbench, Refresh));

					return;

				case VkF12 when control:
					workbench.RunCommand("dotnet.goToImplementation",
						new CommandHost(workbench, Refresh));

					return;

				case VkF12:
					workbench.RunCommand("dotnet.goToDefinition",
						new CommandHost(workbench, Refresh));

					return;

				case VkPlus when control:
				case VkOemPlus when control:
					settings.AdjustZoom(1f);
					ApplyZoom();
					return;

				case VkMinus when control:
				case VkOemMinus when control:
					settings.AdjustZoom(-1f);
					ApplyZoom();
					return;

				case VkNumpad0 when control:
				case Vk0 when control:
					settings.ResetZoom();
					ApplyZoom();
					return;
			}

			if (workbench.ActiveView is not { } view) return;

			float lineHeight = painter.GetCode().GetLineHeight();
			float viewportHeight = workbench.EditorHeight(device.Height);
			int linesPerPage = Math.Max(1, (int)(viewportHeight / lineHeight) - 2);

			switch (key) {
				case VkLeft when control:
					view.MoveWord(-1, shift);
					break;

				case VkRight when control:
					view.MoveWord(1, shift);
					break;

				case VkLeft:
					view.MoveHorizontal(-1, shift);
					break;

				case VkRight:
					view.MoveHorizontal(1, shift);
					break;

				case VkUp when !alt:
					view.MoveVertical(-1, shift);
					break;

				case VkDown when !alt:
					view.MoveVertical(1, shift);
					break;

				case VkPageUp:
					view.MoveVertical(-linesPerPage, shift);
					break;

				case VkPageDown:
					view.MoveVertical(linesPerPage, shift);
					break;

				case VkHome when control:
					view.MoveToDocumentStart(shift);
					break;

				case VkEnd when control:
					view.MoveToDocumentEnd(shift);
					break;

				case VkHome:
					view.MoveToLineStart(shift);
					break;

				case VkEnd:
					view.MoveToLineEnd(shift);
					break;

				case VkBack when control:
					view.DeleteWord(-1);
					workbench.IntelliSense?.Deleted();
					break;

				case VkBack:
					view.Backspace();

					workbench.IntelliSense?.Deleted();
					break;

				case VkDelete when control:
					view.DeleteWord(1);
					workbench.IntelliSense?.Deleted();
					break;

				case VkDelete:
					view.DeleteForward();
					workbench.IntelliSense?.Deleted();
					break;

				case VkReturn when control:
					view.InsertLine(shift ? -1 : 1);
					break;

				case VkReturn:
					view.InsertNewLine();
					break;

				case VkTab when shift:
					view.IndentLines(-1);
					break;

				case VkTab when view.HasSelection:
					view.IndentLines(1);
					break;

				case VkTab:
					view.InsertTab();
					break;

				case VkOemCloseBrackets when control:
					view.IndentLines(1);
					break;

				case VkOemOpenBrackets when control:
					view.IndentLines(-1);
					break;

				case VkK when control && shift:
					view.DeleteLines();
					break;

				case VkOemSlash when control:
					view.ToggleLineComment();
					break;

				case VkL when control:
					view.SelectLine();
					break;

				case VkUp when alt && shift:
				case VkDown when alt && shift:
					view.CopyLines(key == VkUp ? -1 : 1);
					break;

				case VkUp when alt:
				case VkDown when alt:
					view.MoveLines(key == VkUp ? -1 : 1);
					break;

				case VkA when control:
					view.SelectAll();
					break;

				case VkC when control:
					Clipboard.SetText(view.CopyText());
					break;

				case VkX when control:
					Clipboard.SetText(view.CopyText());
					view.Cut();
					break;

				case VkD when control:
					view.SelectNextOccurrence();
					break;

				case VkOemBackslash when control && shift:
					view.GoToMatchingBracket();
					break;

				case VkV when control:
					view.InsertText(Clipboard.GetText());
					break;

				case VkZ when control:
					view.Undo();
					break;

				case VkY when control:
					view.Redo();
					break;

				default:
					return;
			}

			if (key is VkHome or VkEnd or VkLeft or VkRight or VkPageUp or VkPageDown) {
				workbench.IntelliSense?.Suggest.Hide();
			}

			view.EnsureCaretVisible(painter.GetCode(), workbench.TextWidth(device.Width), viewportHeight);

			RefreshEditor();
		};
	}

	private static void LoadExtensions(Workbench workbench, Action refresh, Action<Action> post) {
		CommandHost host = new(workbench, refresh);

		ExtensionHost extensions = new(Settings.Current, BuiltInExtensions.All);
		workbench.ExtensionHost = extensions;

		int built = extensions.RegisterAll(workbench.Commands);

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
		VSCodeBridge bridge = new(workbench.Commands, host);
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

	private static bool HandleQuickOpenKey(Workbench workbench, uint key, AppWindow window, Action refresh) {
		switch (key) {
			case VkEscape:
				workbench.QuickOpen.Hide();
				break;

			case VkUp:
				workbench.QuickOpen.MoveSelection(-1);
				break;

			case VkDown:
				workbench.QuickOpen.MoveSelection(1);
				break;

			case VkBack:
				workbench.QuickOpen.Backspace();
				break;

			case VkReturn:
				if (workbench.QuickOpen.Accept() is { } path) workbench.OpenFile(path);

				workbench.QuickOpen.Hide();
				break;

			default:
				return false;
		}

		refresh();
		return true;
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
