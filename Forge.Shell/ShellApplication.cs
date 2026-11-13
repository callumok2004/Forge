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

		if (UriLauncher.Find(args) is { } launchUri) {
			if (UriLauncher.TryForward(launchUri)) return 0;

			_pendingUris.Add(launchUri);
		}

		AppWindow.FatalHandler = ReportFatal;

		Settings settings = Settings.Load(UserData.SettingsPath);
		SettingsSchema.ExtensionDirectories = [Path.Combine(AppContext.BaseDirectory, "Extensions"), VSCodeExtensions.DefaultDirectory];
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
		workbench.Minimap.Settings = MinimapSettingsFrom(settings);

		using VectorIcon? explorerIcon = VectorIcon.Load(device.Factory, Path.Combine(AppContext.BaseDirectory, "Assets", "ui", "folder.svg"));
		workbench.Auxiliary.IconLoader = path => VectorIcon.Load(device.Factory, path);
		workbench.ImageLoader = path => Path.GetExtension(path.AsSpan()) switch {
			var extension when extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) => SvgImage.Load(device, path),
			var extension when extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".png", StringComparison.OrdinalIgnoreCase) => AnimatedImage.Decode(device, path),
			_ => Image.Decode(device, path),
		};

		ProcessTree processTree = new();
		workbench.ProcessExplorer = new ProcessExplorer(processTree);

		debugger.ProcessStarted += (_, _, pid) => processTree.TargetId = (uint)pid;
		debugger.SessionEnded += _ => {
			processTree.TargetId = 0;
			uiWork.Enqueue(() => workbench.TitleBar.DebugUsage = null);
		};

		using Timer resourceTimer = new(_ => {
			workbench.StatusBar.Resources = processTree.Sample();

			ResourceUsage? target = processTree.Target;
			uiWork.Enqueue(() => workbench.TitleBar.DebugUsage = target);
			window.Invalidate(workbench.StatusBarRect(device.Width, device.Height));

			if (!workbench.ProcessExplorer.IsOpen) return;

			uiWork.Enqueue(workbench.ProcessExplorer.Refresh);
			window.Invalidate();
		}, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));

		string rootPath = ResolveWorkspaceRoot(args);
		workbench.RootPath = rootPath;
		settings.LoadWorkspace(rootPath);
		workbench.Panel.WorkingDirectory = rootPath;
		workbench.Panel.DefaultProfile = settings.DefaultTerminalProfile;
		workbench.Panel.History = new TerminalHistory(UserData.TerminalHistoryPath);

		workbench.Panel.Changed += () =>
			window.Invalidate(workbench.PanelRect(device.Width, device.Height));
		workbench.Panel.Output.Append($"Workspace: {rootPath}");
		workbench.BranchName = GitWorkspaceInfo.ReadBranchName(rootPath);
		workbench.TitleBar.BranchName = workbench.BranchName;

		AttachRepository();
		workbench.Sidebar.SourceControl.LoadState(UserData.SourceControlStatePath);

		void AttachRepository() {
			workbench.Repository = GitRepository.Open(rootPath);
			workbench.Sidebar.Repository = workbench.Repository;
			workbench.Sidebar.SourceControl.SetRoot(workbench.Repository?.GetRootPath() ?? rootPath);

			if (workbench.Repository is not { } repository) return;

			repository.OnChanged(() => {
				workbench.BranchName = repository.GetBranch() ?? workbench.BranchName;
				workbench.TitleBar.BranchName = workbench.BranchName;
				workbench.RefreshDiff(window.Invalidate);
				workbench.RefreshGitDiffs(window.Invalidate);
				window.Invalidate();
			});

			ThreadPool.UnsafeQueueUserWorkItem(_ => repository.Refresh(), null);

			workbench.ActiveDocumentChanged += () => workbench.RefreshDiff(window.Invalidate);
			workbench.ActiveDocumentChanged += () => {
				if (workbench.Panel.ProblemsView.Scope != ErrorListScope.EntireSolution) workbench.Panel.ProblemsView.Sync();
			};
		}

		if (GitWorkspaceInfo.ReadRemote(rootPath) is { } remote) {
			workbench.TitleBar.RemoteOwner = remote.Owner;
			workbench.TitleBar.RemoteRepository = remote.Repository;
		}
		workbench.Sidebar.SetTree(new FileTree(rootPath));

		debugger.Attach(rootPath, UserData.BreakpointsPath);

		WorkspaceMemory memory = WorkspaceMemory.Load(rootPath);
		GlobalMemory global = GlobalMemory.Load(UserData.GlobalStatePath);
		global.TouchFolder(rootPath);
		workbench.Palette.History = global.RecentCommands;
		workbench.PeekRatio = global.PeekRatio;
		workbench.PeekHeightInLines = global.PeekHeightInLines;
		workbench.FileOpened += global.TouchFile;

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

		workbench.Post = Post;

		IOutputChannel gitChannel = workbench.Panel.Channel("Git");
		Git.GitProcess.Log = line => uiWork.Enqueue(() => gitChannel.AppendRaw(line));


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

		DebugConsoleView debugConsole = workbench.Panel.DebugConsole;
		debugConsole.Post = action => workbench.Post(() => {
			action();
			window.Invalidate();
		});
		debugConsole.Evaluate = expression => debugger.EvaluateAsync(expression, "repl");
		debugConsole.LoadChildren = debugger.VariablesAsync;

		debugger.SessionStarted += _ => debugConsole.Post(debugConsole.Clear);
		debugger.OnOutput(value => debugConsole.Append(value.Category, value.Text));

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
		workbench.Sidebar.Debug.OpenBreakpoint = (path, line) => workbench.GoTo(path, line, 1);

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

		ViewPaneContainer.SavedStates = memory.ViewPanes;
		WorkbenchViews views = new(workbench, Post) { Refresh = window.Invalidate, SavedLocations = memory.ViewLocations };
		workbench.ActivityBar.SetCustomOrder(memory.ActivityOrder);
		workbench.ActivityBar.OrderChanged = order => memory.ActivityOrder = [.. order];
		workbench.Auxiliary.Strip.SetCustomOrder(memory.AuxiliaryOrder);
		workbench.Auxiliary.Strip.OrderChanged = order => memory.AuxiliaryOrder = [.. order];
		workbench.Views = views;
		RegisterBuiltInViews(views, workbench, Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar)), explorerIcon);

		SearchView search = workbench.Sidebar.Search;
		search.RootPath = rootPath;
		search.OpenEditorPaths = () => [.. workbench.Documents.Select(document => document.Path).OfType<string>()];
		search.MatchOpened = (path, line, column, length, preserveFocus) => {
			workbench.OpenMatch(path, line, column, length, preview: true);

			if (preserveFocus) search.RequestFocus();

			window.Invalidate();
		};
		search.ExcludeSettings = SearchExcludes;
		search.ReplacePreviewRequested = (path, edits, line) => {
			workbench.OpenReplacePreview(path, edits, line);
			window.Invalidate();
		};
		search.ReplaceRequested = (path, edits) => {
			workbench.ReplaceInFile(path, edits);
			window.Invalidate();
		};

		ThreadPool.UnsafeQueueUserWorkItem(_ => LoadExtensions(workbench, debugger, window.Invalidate, Post), null);

		UriLauncher.Listen(uri => Post(() => {
			OpenUri(uri);
			window.Restore();
		}));

		IFileIndex index = new FileIndex(rootPath);
		workbench.QuickOpen.Attach(index);

		index.OnUpdated(() => {
			ambient.Pulse(AmbientSignal.Search, 0.55f);

			if (workbench.QuickInput.IsOpen && workbench.QuickInput.Provider == workbench.QuickOpen) Post(workbench.QuickInput.Refilter);
		});

		fileIcons.OnReady(window.Invalidate);

		window.Events.PlacementChanged += () => window.CaptureState().Save(UserData.WindowStatePath);

		window.Events.AppActivated += active => {
			if (active || Workbench.AutoSaveMode is not ("onWindowChange" or "onFocusChange")) return;

			workbench.AutoSave();
			window.Invalidate();
		};

		Timer autoSaveTimer = new(_ => Post(() => {
			if (Workbench.AutoSaveMode != "afterDelay") return;

			workbench.AutoSave();
			window.Invalidate();
		}));

		workbench.DocumentAdded += document => document.Buffer.Changed += (_, _) => {
			if (Workbench.AutoSaveMode == "afterDelay") autoSaveTimer.Change(Math.Max(0, Settings.Current.GetInt32("files.autoSaveDelay") ?? 1000), Timeout.Infinite);
		};

		index.StartScan();

		using WorkspaceWatcher watcher = new(rootPath, (directories, git) => {
			if (git && workbench.Repository is { } watched) watched.Refresh();
			else if (git) Post(() => {
				if (workbench.Repository is null) AttachRepository();
			});

			if (directories.Count > 0) Post(() => {
				workbench.Sidebar.Explorer.Reload(directories);
				workbench.ReloadExternalChanges(directories);

				foreach (string directory in directories) {
					if (!Forge.Core.WorkspaceConfig.IsConfigFolder(directory)) continue;

					debugger.ReloadConfigurations();
					Settings.Current.Reload();
					break;
				}
			});
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
			global.PeekRatio = workbench.PeekRatio;
			global.PeekHeightInLines = workbench.PeekHeightInLines;
			global.Save();
			settings.Save();
		}
	}

	private static void RegisterBuiltInViews(WorkbenchViews views, Workbench workbench, string folder, IVectorIcon? explorerIcon) {
		views.Register(new ViewContainer(Sidebar.ExplorerId, folder.Length > 0 ? folder : "Explorer", Codicon.Files, ViewLocation.Sidebar, 0) { VectorIcon = explorerIcon });
		views.Register(new ViewContainer(Sidebar.SearchId, "Search", Codicon.Search, ViewLocation.Sidebar, ActivityBar.SearchOrder));
		views.Register(new ViewContainer(Sidebar.SourceControlId, "Source Control", Codicon.SourceControl, ViewLocation.Sidebar, 20));
		views.Register(new ViewContainer(Sidebar.RunId, "Run and Debug", Codicon.DebugAlt, ViewLocation.Sidebar, 30));

		views.AddView(new ViewDescriptor("workbench.explorer.fileView", "Folders", Sidebar.ExplorerId, () => workbench.Sidebar.Explorer) {
			Actions = [
				new ViewAction("workbench.files.action.refreshFilesExplorer", Codicon.Refresh, "Refresh Explorer"),
				new ViewAction("workbench.files.action.collapseExplorerFolders", Codicon.CollapseAll, "Collapse Folders in Explorer"),
			],
		});
		views.AddView(new ViewDescriptor("workbench.view.search.view", "Search", Sidebar.SearchId, () => workbench.Sidebar.Search) { TitleControls = () => [.. workbench.Sidebar.Search.HeaderActions] });
		views.AddView(new ViewDescriptor("workbench.scm", "Changes", Sidebar.SourceControlId, () => workbench.Sidebar.SourceControl, 0) { TitleControls = () => [.. workbench.Sidebar.SourceControl.HeaderActions] });
		views.AddView(new ViewDescriptor("workbench.scm.history", "Graph", Sidebar.SourceControlId, () => workbench.Sidebar.Graph, 2) { Actions = [new ViewAction("workbench.scm.history.refresh", Codicon.Refresh, "Refresh")] });
		views.AddView(new ViewDescriptor("workbench.debug.variablesView", "Run and Debug", Sidebar.RunId, () => workbench.Sidebar.Debug));
		views.ShowContainer(Sidebar.ExplorerId);
	}

	private static string ContainerFor(string? saved) => saved switch {
		null or "" or nameof(ActivityView.Explorer) => Sidebar.ExplorerId,
		nameof(ActivityView.SourceControl) => Sidebar.SourceControlId,
		nameof(ActivityView.Search) => Sidebar.SearchId,
		nameof(ActivityView.Run) => Sidebar.RunId,
		_ => saved,
	};

	private static void RestoreWorkspace(Workbench workbench, WorkspaceMemory memory) {
		workbench.ViewStates = memory.ViewStates;
		workbench.QuickOpen.History = memory.QuickOpenHistory;
		workbench.Sidebar.RestoreExpanded(memory.ExpandedFolders);
		if (memory.SidebarWidth >= Metrics.SidebarMinWidth) workbench.Sidebar.Width = memory.SidebarWidth;

		if (workbench.Views is { } views && views.LocationOf(ContainerFor(memory.SidebarView)) == ViewLocation.Sidebar) views.ShowContainer(ContainerFor(memory.SidebarView));

		workbench.ShowSidebar = memory.ShowSidebar;

		if (memory.PanelHeight > 0f) workbench.Panel.Height = memory.PanelHeight;

		workbench.Panel.RestoreGroups(memory.PanelGroups);

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

		workbench.CaptureViewStates();
		workbench.Sidebar.CaptureExpanded(memory.ExpandedFolders);
		memory.ActiveFile = workbench.ActiveFilePath;
		memory.ShowSidebar = workbench.ShowSidebar;
		memory.SidebarWidth = workbench.Sidebar.Width;
		memory.SidebarView = workbench.Sidebar.ActiveContainer;
		memory.PanelOpen = workbench.Panel.IsOpen;
		memory.PanelView = workbench.Panel.Active.ToString();
		memory.PanelGroups = [.. workbench.Panel.SaveGroups()];
		memory.PanelHeight = workbench.Panel.Height;
		memory.AuxiliaryOpen = workbench.Auxiliary.IsOpen;
		memory.AuxiliaryWidth = workbench.Auxiliary.Width;
		memory.AuxiliaryView = workbench.Auxiliary.ActiveViewId;
		memory.DebugConfiguration = debugger.GetSelectedConfiguration();
	}

	private static readonly string[] DefaultFilesExclude = ["**/.git", "**/.svn", "**/.hg", "**/.DS_Store", "**/Thumbs.db"];
	private static readonly string[] DefaultSearchExclude = ["**/node_modules", "**/bower_components", "**/*.code-search"];

	private static IReadOnlyList<string> SearchExcludes() {
		Dictionary<string, bool> patterns = [];

		foreach (string pattern in DefaultFilesExclude) patterns[pattern] = true;
		foreach (string pattern in DefaultSearchExclude) patterns[pattern] = true;

		foreach (string key in (string[])["files.exclude", "search.exclude"]) {
			if (Settings.Current.GetRaw(key) is not System.Text.Json.Nodes.JsonObject entries) continue;

			foreach ((string pattern, System.Text.Json.Nodes.JsonNode? value) in entries) {
				if (value is System.Text.Json.Nodes.JsonValue flag && flag.TryGetValue(out bool enabled)) patterns[pattern] = enabled;
			}
		}

		List<string> result = [];

		foreach ((string pattern, bool enabled) in patterns) {
			if (enabled) result.Add(pattern);
		}

		return result;
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


	private static MinimapSettings MinimapSettingsFrom(Settings settings) => new() {
		Enabled = settings.MinimapEnabled,
		Autohide = settings.MinimapAutohide,
		Size = settings.MinimapSize,
		Side = settings.MinimapSide,
		ShowSlider = settings.MinimapShowSlider,
		Scale = settings.MinimapScale,
		RenderCharacters = settings.MinimapRenderCharacters,
		MaxColumn = settings.MinimapMaxColumn,
		ShowRegionSectionHeaders = settings.MinimapShowRegionSectionHeaders,
		ShowMarkSectionHeaders = settings.MinimapShowMarkSectionHeaders,
		MarkSectionHeaderRegex = settings.MinimapMarkSectionHeaderRegex,
		SectionHeaderFontSize = settings.MinimapSectionHeaderFontSize,
		SectionHeaderLetterSpacing = settings.MinimapSectionHeaderLetterSpacing,
	};

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

		WorkbenchPanel root = new(workbench);
		ui.Root.AddChild(root);

		if (workbench.Views is { } views) {
			views.Refresh = Refresh;
			views.WindowSize = () => (device.Width, device.Height);
			views.OverlayParent = root;
			views.ContainerShown = id => {
				if (id != Sidebar.SourceControlId) return;

				workbench.Sidebar.SourceControl.SetRoot(workbench.Repository?.GetRootPath() ?? rootPath);
				Registry.Execute("git.refresh", new CommandHost(workbench, Refresh));
			};
			workbench.ActivityBar.ContainerSelected = views.ToggleContainer;
		}

		workbench.ActivityBar.ViewSelected = view => {
			string id = "workbench.view.extensions";

			if (workbench.ActivityBar.ActiveId == id) workbench.ShowSidebar = !workbench.ShowSidebar;
			else {
				workbench.ActivityBar.ActiveId = id;
				workbench.Sidebar.ShowContainer(Sidebar.ExplorerId);
				workbench.ShowSidebar = true;
			}

			Refresh();
		};
		ui.CommandHandler = (command, _) => workbench.RunCommand(command, new CommandHost(workbench, Refresh));


		void RefreshEditor() {
			UpdateTitle(window, workbench, rootPath);

			editorDamage.Clear();
			workbench.AddEditorDamage(editorDamage, painter, device.Width, device.Height);

			for (int i = 0; i < editorDamage.Count; i++) Damage(editorDamage[i]);
		}

		workbench.IntelliSense = new IntelliSense(workbench, Refresh) {
			DetailsVisibleChanged = settings.SetSuggestDetailsVisible,
			Debug = debugger,
		};

		workbench.IntelliSense.AddFeatures(new JsonSchemaFeatures(path => workbench.Documents.FirstOrDefault(document => string.Equals(document.Path, path, StringComparison.OrdinalIgnoreCase))?.Buffer.GetText(), () => workbench.RootPath));

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

		settings.Changed += _ => {
			if (fonts.Configure(settings.EditorFontFamily, settings.EditorFontSize, settings.EditorLineHeight, settings.UiFontFamily, settings.UiFontSize)) {
				if (workbench.Webviews is Forge.Webview.WebviewHost host) host.ThemeCss = Common.Webview.WebviewTheme.BuildCss(settings.EditorFontFamily[0], settings.EditorFontSize);
			}

			workbench.ShowMinimap = settings.MinimapEnabled;
			workbench.Minimap.Settings = MinimapSettingsFrom(settings);
			workbench.Panel.DefaultProfile = settings.DefaultTerminalProfile;
			workbench.IntelliSense.Suggest.DetailsVisible = settings.SuggestDetailsVisible;
			ApplyZoom();
			UpdateTitle(window, workbench, rootPath);
			Refresh();
		};

		settings.Watch(uiWork.Enqueue);

		ShellContext context = new(workbench, window, device, painter, settings, debugger, rootPath) {
			Refresh = Refresh,
			RefreshEditor = RefreshEditor,
			ApplyZoom = ApplyZoom,
			Damage = Damage,
			SetCodeZoom = fonts.SetCodeZoom,
		};

		workbench.Panel.ProblemsChanged += () => workbench.Sidebar.Explorer.SetProblems(workbench.Panel.Problems, workbench.RootPath);
		workbench.Panel.ProblemsChanged += Refresh;
		workbench.Lenses.Post = workbench.Post;
		workbench.Lenses.Changed = Refresh;
		workbench.Sidebar.Explorer.FileOpened = path => {
			workbench.OpenFile(path, preview: true);
			Refresh();
		};
		workbench.Sidebar.Explorer.FilePinned = path => {
			workbench.OpenFile(path, preview: false);
			Refresh();
		};
		ExplorerView explorer = workbench.Sidebar.Explorer;
		explorer.RootPath = workbench.RootPath;
		explorer.RevealRequested = path => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false })?.Dispose();
		explorer.TerminalRequested = folder => {
			workbench.Panel.CreateTerminal(Forge.Terminal.TerminalProfiles.Default(workbench.Panel.Profiles, workbench.Panel.DefaultProfile), folder, null, show: true);

			workbench.PanelFocused = true;
			Refresh();
		};
		explorer.FindInFolderRequested = folder => {
			workbench.Views?.ShowContainer(Sidebar.SearchId);
			workbench.ShowSidebar = true;
			workbench.Sidebar.Search.FocusFolder(System.IO.Path.GetRelativePath(workbench.RootPath ?? folder, folder));
			Refresh();
		};
		explorer.CompareRequested = (original, modified) => {
			workbench.CompareFiles(original, modified);
			Refresh();
		};
		explorer.Renamed = (from, to) => {
			workbench.PathMoved(from, to);
			Refresh();
		};
		explorer.Deleted = path => {
			workbench.PathDeleted(path);
			Refresh();
		};
		workbench.Tabs.Pinned = index => {
			workbench.Pin(index);
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

		DebugStopPresenter stops = new(workbench, context, window.Handle);
		workbench.DebugStops = stops;
		workbench.Peek = new PeekPresenter(workbench, context);
		debugger.RevealSource = reveal => workbench.Post(() => stops.Reveal(reveal));
		debugger.ExceptionCleared = () => workbench.Post(stops.Clear);

		if (workbench.IntelliSense is { } languageUi) {
			languageUi.Actions.Changed = root.Editor.Repaint;
			languageUi.Navigation.LinkChanged = root.Editor.SyncLink;
		}
		workbench.RevealCaret = context.RevealCaret;
		root.Editor.RequestFocus();

		workbench.Panel.WorkspaceRoot = workbench.RootPath;
		workbench.Panel.ActiveFile = () => workbench.ActiveDocument?.Path;
		workbench.Panel.OpenFiles = () => workbench.Documents.Select(static document => document.Path).OfType<string>();

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

		workbench.Tabs.ContextMenu = (index, x, y) => {
			string? path = index < workbench.Documents.Count && workbench.FindPaneAt(index) is null ? workbench.Documents[index].Path : null;
			Menu menu = new();
			menu.Add(new MenuItem("Close", "workbench.action.closeActiveEditor", () => workbench.Close(index)));
			menu.Add(new MenuItem("Close Others", "workbench.action.closeOtherEditors", () => workbench.CloseOthers(index)));
			menu.Add(new MenuItem("Close to the Right", "workbench.action.closeEditorsToTheRight", index < workbench.Documents.Count - 1 ? () => workbench.CloseToTheRight(index) : null));
			menu.Add(new MenuItem("Close Saved", "workbench.action.closeUnmodifiedEditors", workbench.CloseSaved));
			menu.Add(new MenuItem("Close All", "workbench.action.closeAllEditors", workbench.CloseAll));
			menu.AddSeparator();
			menu.Add(new MenuItem("Copy Path", "copyFilePath", path is null ? null : () => Clipboard.SetText(path)));
			menu.Add(new MenuItem("Copy Relative Path", "copyRelativeFilePath", path is null ? null : () => Clipboard.SetText(workbench.RootPath is { } root ? Path.GetRelativePath(root, path) : path)));
			menu.AddSeparator();
			menu.Add(new MenuItem("Reveal in File Explorer", "revealFileInOS", path is null ? null : () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })));
			menu.Add(new MenuItem("Reveal in Explorer View", null, path is null ? null : () => {
				workbench.Views?.ShowContainer(Sidebar.ExplorerId);
				workbench.Sidebar.SelectedPath = path;
			}));
			menu.Open(x, y);
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
			workbench.ExtensionSync?.Tick();

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

			if (workbench.FocusedView is not { } blinking) return;

			blinking.CaretVisible = !blinking.CaretVisible;

			Damage(blinking.RenderedCaretRect(painter.GetCode()));
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

				if (repaint.IsEmpty || !device.BeginPass(repaint.Left, repaint.Top, repaint.Right, repaint.Bottom)) continue;

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
				device.EndPass();
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

			if (rendering) {
				window.Invalidate();
				return;
			}

			rendering = true;

			try {
				pending.Add(PixelRect.FromSize(device.Width, device.Height));
				RenderFrame(PixelRect.FromSize(device.Width, device.Height));
			}
			finally {
				rendering = false;
			}
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

		window.Events.MouseHorizontalWheel += (delta, x, _) => {
			TextView? target = workbench.ActiveDiff is { } diff && diff.IsInOriginal(x) ? diff.Original : workbench.ActiveView;
			target?.ScrollByHorizontalWheel(delta, painter.GetCode().GetCellWidth());
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

		window.Events.MouseDown += (button, x, y) => {
			if (button is MouseButton.Back or MouseButton.Forward) {
				if (Settings.Current.GetBoolean("workbench.editor.mouseBackForwardToNavigate") ?? true) workbench.RunCommand(button == MouseButton.Back ? "workbench.action.navigateBack" : "workbench.action.navigateForward", context.Host);

				return;
			}

			if (workbench.Auxiliary.ShownViewId is not null && !workbench.Auxiliary.ContentBounds.Contains(new PixelRect(x, y, x + 1, y + 1))) workbench.Webviews.ReleaseFocus();

			if (ui.InternalMousePressed(button, x, y)) return;

			workbench.IntelliSense?.Dismiss();
			workbench.PanelFocused = false;
		};

		window.Events.MouseUp += (button, x, y) => {
			if (button is not (MouseButton.Back or MouseButton.Forward)) ui.InternalMouseReleased(button, x, y);
		};

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

	private static void LoadExtensions(Workbench workbench, IDebug debugger, Action refresh, Action<Action> post) {
		CommandHost host = new(workbench, refresh);

		ExtensionHost extensions = new(Settings.Current, BuiltInExtensions.All) { Views = workbench.Views, Debug = debugger };
		workbench.ExtensionHost = extensions;

		int built = extensions.RegisterAll(Registry);

		TrailingSpacesExtension trailing = BuiltInExtensions.Get<TrailingSpacesExtension>();

		workbench.TrailingSpaces = trailing;
		workbench.Presence = BuiltInExtensions.Get<DiscordPresenceExtension>();

		workbench.WillSave = text => {
			if (!trailing.TrimOnSave) return null;

			List<TrailingRange> ranges =
				TrailingSpacesExtension.Find(text, skipLine: workbench.ActiveView?.CaretLine, includeEmptyLines: true);

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

		StartVSCodeBridge(workbench, host, post, refresh);
	}

	private static void StartVSCodeBridge(Workbench workbench, CommandHost host, Action<Action> post, Action refresh) {
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
			Configuration = Settings.Current.Snapshot(),
		};

		Settings.Current.Changed += _ => post(() => bridge.Notify("host/state", new System.Text.Json.Nodes.JsonObject { ["configuration"] = Settings.Current.Snapshot() }));

		ExtensionHostSync sync = new(workbench, bridge, post, refresh);
		bridge.ContextChanged = () => post(() => workbench.SyncEditorActions());

		bridge.Start(
			Path.Combine(UserData.Directory, "vscode-storage"),
			state,
			VSCodeExtensions.DefaultDirectory);

		post(() => {
			workbench.ExtensionSync = sync;
			sync.Start();
			_uriBridge = bridge;

			foreach (string uri in _pendingUris) OpenUri(uri);

			_pendingUris.Clear();
		});
	}

	private static readonly List<string> _pendingUris = [];
	private static VSCodeBridge? _uriBridge;

	private static void OpenUri(string uri) {
		if (_uriBridge is not { } bridge) {
			_pendingUris.Add(uri);
			return;
		}

		_ = bridge.RequestAsync("host/openUri", new System.Text.Json.Nodes.JsonObject { ["uri"] = uri });
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
