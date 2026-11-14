using System.Diagnostics;

using Forge.Common.Commands;
using Forge.Common.Debug;
using Forge.Core;
using Forge.Core.Native;
using Forge.Debug;
using Forge.UI;
using Forge.UI.Controls;

namespace Forge.Shell.Actions;

public static class WorkbenchCommands
{
	public static void Register(ICommandRegistry registry, ShellContext context) {
		Workbench workbench = context.Workbench;

		void Add(string id, string title, string category, Action action, string? keybinding = null, Func<bool>? when = null) => registry.Register(new Command(id, title, category, _ => {
			action();
			context.Refresh();
		}, keybinding, when));

		Add("workbench.action.files.openFile", "Open File...", "File", () => {
			if (FileDialog.OpenFile(context.Window.Handle) is { } path) workbench.OpenFile(path);
		}, "Ctrl+O");

		Add("workbench.action.files.openFolder", "Open Folder...", "File", () => {
			if (FileDialog.OpenFolder(context.Window.Handle) is { } folder) Launch(Environment.ProcessPath!, folder);
		});

		Add("workbench.action.newWindow", "New Window", "File", () => Launch(Environment.ProcessPath!), "Ctrl+Shift+N");
		Add("workbench.action.files.save", "Save", "File", () => workbench.SaveActive(), "Ctrl+S");

		Add("workbench.action.files.saveAs", "Save As...", "File", () => {
			if (workbench.ActiveDocument is { } document && FileDialog.SaveFile(context.Window.Handle, document.Name) is { } target) document.SaveAs(target);
		}, "Ctrl+Shift+S");

		Add("workbench.action.closeActiveEditor", "Close Editor", "View", workbench.CloseActive, "Ctrl+W");
		Add("workbench.action.closeAllEditors", "Close All Editors", "View", workbench.CloseAll, "Ctrl+K Ctrl+W");
		Add("workbench.action.closeOtherEditors", "Close Other Editors in Group", "View", () => workbench.CloseOthers(workbench.ActiveIndex));
		Add("workbench.action.closeEditorsToTheRight", "Close Editors to the Right in Group", "View", () => workbench.CloseToTheRight(workbench.ActiveIndex));
		Add("workbench.action.closeEditorsToTheLeft", "Close Editors to the Left in Group", "View", () => workbench.CloseToTheLeft(workbench.ActiveIndex));
		Add("workbench.action.closeUnmodifiedEditors", "Close Saved Editors in Group", "View", workbench.CloseSaved, "Ctrl+K U");
		Add("workbench.action.reopenClosedEditor", "Reopen Closed Editor", "View", workbench.ReopenClosed, "Ctrl+Shift+T");
		Add("workbench.action.nextEditor", "Open Next Editor", "View", () => workbench.ActivateRelative(1), "Ctrl+PageDown");
		Add("workbench.action.previousEditor", "Open Previous Editor", "View", () => workbench.ActivateRelative(-1), "Ctrl+PageUp");

		for (int i = 1; i <= 9; i++) {
			int index = i - 1;
			Add($"workbench.action.openEditorAtIndex{i}", $"Open Editor at Index {i}", "View", () => workbench.Activate(index), $"Alt+{i}");
		}

		Add("workbench.action.lastEditorInGroup", "Open Last Editor in Group", "View", () => workbench.Activate(workbench.Documents.Count - 1), "Alt+0");
		Add("workbench.action.files.saveAll", "Save All", "File", workbench.SaveAll, "Ctrl+K S");
		Add("workbench.action.files.revert", "Revert File", "File", workbench.RevertActive);
		ExplorerView explorer = workbench.Sidebar.Explorer;
		bool ExplorerFocused() => !explorer.IsEditing && (Common.UI.Panel.Ui?.HasContext(ExplorerView.FocusContext) ?? false);

		Add("explorer.newFile", "New File...", "File", explorer.NewFile);
		Add("explorer.newFolder", "New Folder...", "File", explorer.NewFolder);
		Add("revealFileInOS", "Reveal in File Explorer", "File", explorer.RevealInOS, "Shift+Alt+R", ExplorerFocused);
		Add("openInIntegratedTerminal", "Open in Integrated Terminal", "File", explorer.OpenInTerminal);
		Add("selectForCompare", "Select for Compare", "File", explorer.SelectForCompare);
		Add("compareFiles", "Compare with Selected", "File", explorer.CompareWithSelected);
		Add("filesExplorer.findInFolder", "Find in Folder...", "File", explorer.FindInFolder, "Shift+Alt+F", ExplorerFocused);
		Add("filesExplorer.cut", "Cut", "File", explorer.Cut, "Ctrl+X", ExplorerFocused);
		Add("filesExplorer.copy", "Copy", "File", explorer.Copy, "Ctrl+C", ExplorerFocused);
		Add("filesExplorer.paste", "Paste", "File", explorer.Paste, "Ctrl+V", ExplorerFocused);
		Add("copyFilePath", "Copy Path", "File", explorer.CopyPath, "Shift+Alt+C", ExplorerFocused);
		Add("copyRelativeFilePath", "Copy Relative Path", "File", explorer.CopyRelativePath, "Ctrl+K Ctrl+Shift+C", ExplorerFocused);
		Add("renameFile", "Rename...", "File", explorer.Rename, "F2", ExplorerFocused);
		Add("deleteFile", "Delete", "File", explorer.Delete, "Delete", ExplorerFocused);

		Add("workbench.files.action.collapseExplorerFolders", "Collapse Folders in Explorer", "File", workbench.Sidebar.Explorer.CollapseAll);
		Add("workbench.files.action.refreshFilesExplorer", "Refresh Explorer", "File", () => {
			if (workbench.RootPath is { } root) workbench.Sidebar.Explorer.Reload([root]);
		});

		Add("copyFilePath", "Copy Path of Active File", "File", () => {
			if (workbench.ActiveDocument?.Path is { } path) Clipboard.SetText(path);
		}, "Shift+Alt+C");

		Add("copyRelativeFilePath", "Copy Relative Path of Active File", "File", () => {
			if (workbench.ActiveDocument?.Path is { } path) Clipboard.SetText(workbench.RootPath is { } root ? Path.GetRelativePath(root, path) : path);
		}, "Ctrl+K Ctrl+Shift+Alt+C");

		Add("revealFileInOS", "Reveal in File Explorer", "File", () => {
			if (workbench.ActiveDocument?.Path is { } path) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
		}, "Shift+Alt+R");

		Add("workbench.view.explorer", "Show Explorer", "View", () => workbench.Views?.ShowContainer(Sidebar.ExplorerId), "Ctrl+Shift+E");
		Add("workbench.view.scm", "Show Source Control", "View", () => workbench.Views?.ShowContainer(Sidebar.SourceControlId), "Ctrl+Shift+G");
		Add("workbench.view.debug", "Show Run and Debug", "View", () => workbench.Views?.ShowContainer(Sidebar.RunId), "Ctrl+Shift+D");
		Add("workbench.action.openSettings", "Open Settings", "Preferences", () => {
			SettingsEditorPane pane = workbench.FindPane(SettingsEditorPane.PaneId) as SettingsEditorPane ?? new SettingsEditorPane(path => {
				workbench.OpenFile(path);
				context.Refresh();
			});
			workbench.OpenPane(pane);
			pane.FocusSearch();
		}, "Ctrl+,");
		Add("workbench.action.openSettingsJson", "Open User Settings (JSON)", "Preferences", () => workbench.OpenFile(UserData.SettingsPath));
		Add("workbench.action.quit","Quit", "File", context.Window.CloseWindow);

		Add("workbench.action.showCommands", "Show All Commands", "View", () => workbench.QuickInput.Show(workbench.Palette), "Ctrl+Shift+P");
		Add("workbench.action.quickOpen", "Go to File...", "Go", () => workbench.QuickInput.Show(workbench.QuickOpen), "Ctrl+P");
		GoToLineProvider goToLine = new(workbench);
		Add("workbench.action.gotoLine", "Go to Line/Column...", "Go", () => workbench.QuickInput.Show(goToLine), "Ctrl+G");
		Add("workbench.action.navigateBack", "Go Back", "Go", workbench.History.GoBack, "Alt+Left", () => workbench.History.CanGoBack);
		Add("workbench.action.navigateForward", "Go Forward", "Go", workbench.History.GoForward, "Alt+Right", () => workbench.History.CanGoForward);

		Add("actions.find", "Find", "Editor", () => {
			workbench.Find.Show(workbench.ActiveView);
			context.ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
		}, "Ctrl+F");

		Add("editor.action.startFindReplaceAction", "Replace", "Editor", () => {
			workbench.Find.ShowReplace(workbench.ActiveView);
			context.ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
		}, "Ctrl+H");

		Add("workbench.action.toggleSidebarVisibility", "Toggle Primary Side Bar Visibility", "View", () => workbench.ShowSidebar = !workbench.ShowSidebar, "Ctrl+B");
		Add("workbench.action.toggleAuxiliaryBar", "Toggle Secondary Side Bar Visibility", "View", () => workbench.Auxiliary.Toggle(), "Ctrl+Alt+B");

		Add("workbench.action.togglePanel", "Toggle Panel Visibility", "View", () => {
			workbench.Panel.IsOpen = !workbench.Panel.IsOpen;
			workbench.PanelFocused = workbench.Panel.IsOpen;
		}, "Ctrl+J");

		Add("workbench.action.terminal.toggleTerminal", "Toggle Terminal", "View", () => {
			workbench.Panel.Toggle(PanelView.Terminal);
			workbench.PanelFocused = workbench.Panel.IsOpen;
		}, "Ctrl+`");

		Add("workbench.action.findInFiles", "Search: Find in Files", "Search", () => {
			string? seed = workbench.ActiveView is { HasSelection: true } view && view.GetSelectedText() is { } selected && !selected.Contains('\n') ? selected : null;
			workbench.Views?.ShowContainer(Sidebar.SearchId);
			workbench.ShowSidebar = true;
			workbench.Sidebar.Search.Focus(seed);
		}, "Ctrl+Shift+F");

		Add("workbench.view.search", "Show Search", "View", () => {
			workbench.Views?.ShowContainer(Sidebar.SearchId);
			workbench.ShowSidebar = true;
			workbench.Sidebar.Search.Focus(null);
		});

		Add("workbench.actions.view.problems", "Focus Problems", "View", () => ShowPanel(workbench, PanelView.Problems), "Ctrl+Shift+M");
		Add("workbench.action.output.toggleOutput", "Toggle Output", "View", () => ShowPanel(workbench, PanelView.Output), "Ctrl+Shift+U");

		Add("editor.action.toggleMinimap", "Toggle Minimap", "View", () => {
			workbench.ShowMinimap = !workbench.ShowMinimap;
			context.Settings.SetMinimapEnabled(workbench.ShowMinimap);
		});

		Add("workbench.action.openProcessExplorer", "Open Process Explorer", "Developer", () => workbench.ProcessExplorer?.Toggle());

		Add("forge.toggleFrameStats", "Toggle Frame Statistics", "Developer", () => workbench.ShowFrameStats = !workbench.ShowFrameStats, "Ctrl+Shift+F12");

		Add("editor.action.fontZoomIn", "Increase Editor Font Size", "Editor", () => context.ZoomEditor(1));
		Add("editor.action.fontZoomOut", "Decrease Editor Font Size", "Editor", () => context.ZoomEditor(-1));
		Add("editor.action.fontZoomReset", "Reset Editor Font Size", "Editor", () => context.ZoomEditor(0));

		Add("workbench.action.zoomIn", "Zoom In", "View", () => Zoom(context, 1f), "Ctrl+=");
		Add("workbench.action.zoomOut", "Zoom Out", "View", () => Zoom(context, -1f), "Ctrl+-");

		Add("workbench.action.zoomReset", "Reset Zoom", "View", () => {
			context.Settings.ResetZoom();
			context.ApplyZoom();
		}, "Ctrl+0");

		Add("forge.titleBar.openMenu", "Open Application Menu", "View", () => context.OpenMenu(ApplicationMenu.CodeMenu, workbench.TitleBar.MenuLeft));
		Add("forge.titleBar.openProjectMenu", "Open Project Menu", "View", () => context.OpenMenu(ProjectMenu(workbench.TitleBar), workbench.TitleBar.ProjectLeft));

		Add("forge.openRemote", "Open on GitHub", "Git", () => {
			if (context.Host.GetRemoteUrl() is { } url) Launch(url);
		}, when: () => context.Host.GetRemoteUrl() is not null);

		Add("forge.copyRemote", "Copy Repository URL", "Git", () => {
			if (context.Host.GetRemoteUrl() is { } url) Clipboard.SetText(url);
		}, when: () => context.Host.GetRemoteUrl() is not null);

		Add("forge.debug.pickConfiguration", "Select Launch Configuration", "Debug", () => {
			LaunchPanel? panel = null;
			panel = new LaunchPanel(context.Debugger, () => panel?.PostCommand(DebugCommands.Configure));
			panel.Open(workbench.TitleBar.RunConfigurationLeft, TitleBar.Height(workbench.Scale));
		});
	}

	private static MenuEntry[] ProjectMenu(TitleBar bar) {
		MenuEntry[] common = [new("Open Folder…", "workbench.action.files.openFolder"), new("New Window", "workbench.action.newWindow")];

		return bar.RemoteRepository is { Length: > 0 } ? [.. common, MenuEntry.Separator, new("Open on GitHub", "forge.openRemote"), new("Copy Repository URL", "forge.copyRemote")] : common;
	}

	private static void ShowPanel(Workbench workbench, PanelView view) {
		workbench.Panel.Show(view);
		workbench.PanelFocused = false;
	}

	private static void Zoom(ShellContext context, float delta) {
		context.Settings.AdjustZoom(delta);
		context.ApplyZoom();
	}

	private static void Launch(string fileName, string? arguments = null) => Process.Start(new ProcessStartInfo(fileName, arguments ?? string.Empty) { UseShellExecute = true });
}
