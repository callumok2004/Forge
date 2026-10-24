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
		Add("workbench.action.openSettings", "Open Settings", "Preferences", () => workbench.OpenFile(UserData.SettingsPath), "Ctrl+,");
		Add("workbench.action.quit", "Quit", "File", context.Window.CloseWindow);

		Add("workbench.action.showCommands", "Show All Commands", "View", () => workbench.QuickInput.Show(workbench.Palette), "Ctrl+Shift+P");
		Add("workbench.action.quickOpen", "Go to File...", "Go", () => workbench.QuickInput.Show(workbench.QuickOpen), "Ctrl+P");

		Add("actions.find", "Find", "Editor", () => {
			workbench.Find.Show(workbench.ActiveView);
			context.ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
		}, "Ctrl+F");

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

		Add("workbench.actions.view.problems", "Focus Problems", "View", () => ShowPanel(workbench, PanelView.Problems), "Ctrl+Shift+M");
		Add("workbench.action.output.toggleOutput", "Toggle Output", "View", () => ShowPanel(workbench, PanelView.Output), "Ctrl+Shift+U");

		Add("editor.action.toggleMinimap", "Toggle Minimap", "View", () => {
			workbench.ShowMinimap = !workbench.ShowMinimap;
			context.Settings.SetMinimapEnabled(workbench.ShowMinimap);
		});

		Add("workbench.action.openProcessExplorer", "Open Process Explorer", "Developer", () => workbench.ProcessExplorer?.Toggle());

		Add("forge.toggleFrameStats", "Toggle Frame Statistics", "Developer", () => workbench.ShowFrameStats = !workbench.ShowFrameStats, "Ctrl+Shift+F12");

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
			Menu menu = new();
			string? selected = context.Debugger.GetSelectedConfiguration();

			foreach (DebugConfiguration configuration in context.Debugger.GetConfigurations()) {
				string name = configuration.Name;
				menu.Add(new MenuItem(name, action: () => context.Debugger.SelectConfiguration(name)) { Checked = name == selected });
			}

			menu.AddSeparator();
			menu.Add(new MenuItem("Edit Configurations...", DebugCommands.Configure));

			context.OpenMenu(menu, workbench.TitleBar.RunConfigurationLeft, TitleBar.Height(workbench.Scale));
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
