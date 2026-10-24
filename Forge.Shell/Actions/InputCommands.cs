using Forge.Common.Commands;

namespace Forge.Shell.Actions;

public static class InputCommands
{
	public static void Register(ICommandRegistry registry, ShellContext context) {
		Workbench workbench = context.Workbench;

		void Add(string id, string title, string category, string keybinding, string target, Action action) => registry.Register(new Command(id, title, category, _ => {
			action();
			context.Refresh();
		}, keybinding, () => context.IsFocused(target)));

		Add("workbench.action.closeQuickOpen", "Close Quick Open", "View", "Escape", InputTargets.QuickInput, () => {
			workbench.Palette.Hide();
			workbench.QuickOpen.Hide();
		});

		Add("workbench.action.quickOpenSelectNext", "Select Next in Quick Open", "View", "Down", InputTargets.QuickInput, () => MoveQuickInput(workbench, 1));
		Add("workbench.action.quickOpenSelectPrevious", "Select Previous in Quick Open", "View", "Up", InputTargets.QuickInput, () => MoveQuickInput(workbench, -1));
		Add("workbench.action.acceptSelectedQuickOpenItem", "Accept Quick Open Item", "View", "Enter", InputTargets.QuickInput, () => AcceptQuickInput(context));

		Add("closeFindWidget", "Close Find Widget", "Editor", "Escape", InputTargets.FindWidget, workbench.Find.Hide);
		Add("editor.action.nextMatchFindAction", "Find Next", "Editor", "Enter|F3", InputTargets.FindWidget, () => context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, 1)));
		Add("editor.action.previousMatchFindAction", "Find Previous", "Editor", "Shift+Enter|Shift+F3", InputTargets.FindWidget, () => context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, -1)));

		Add("toggleFindCaseSensitive", "Toggle Match Case", "Editor", "Alt+C", InputTargets.FindWidget, () => {
			workbench.Find.ToggleMatchCase();
			context.ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
		});

		Add("forge.processExplorer.close", "Close Process Explorer", "Developer", "Escape", InputTargets.ProcessExplorer, () => workbench.ProcessExplorer?.Close());

		Add("forge.menu.close", "Close Menu", "View", "Escape", InputTargets.Menu, context.CloseMenu);
		Add("forge.menu.selectNext", "Select Next Menu Item", "View", "Down", InputTargets.Menu, () => workbench.Menu.MoveSelection(1));
		Add("forge.menu.selectPrevious", "Select Previous Menu Item", "View", "Up", InputTargets.Menu, () => workbench.Menu.MoveSelection(-1));
		Add("forge.menu.expand", "Open Submenu", "View", "Right", InputTargets.Menu, () => workbench.Menu.MoveHorizontal(1));
		Add("forge.menu.collapse", "Close Submenu", "View", "Left", InputTargets.Menu, () => workbench.Menu.MoveHorizontal(-1));

		registry.Register(new Command("forge.menu.accept", "Accept Menu Item", "View", _ => {
			if (workbench.Menu.Accept() is { } accepted) context.Invoke(accepted);
			else context.Refresh();
		}, "Enter", () => context.IsFocused(InputTargets.Menu)));

		Add("forge.gitMenu.close", "Close Source Control Menu", "View", "Escape", InputTargets.GitMenu, workbench.Sidebar.SourceControl.Menu.Close);

		Add("scm.acceptInput", "Commit", "Source Control", "Ctrl+Enter", InputTargets.ScmInput, () => SourceControlCommands.HandleCommit(workbench, context.Refresh));
		Add("forge.scm.insertNewLine", "Insert New Line", "Source Control", "Enter", InputTargets.ScmInput, () => workbench.Sidebar.SourceControl.Append('\n'));
		Add("forge.scm.blurInput", "Leave Commit Message", "Source Control", "Escape", InputTargets.ScmInput, () => workbench.Sidebar.SourceControl.FocusMessage(false));

		Add("workbench.action.terminal.focusEditor", "Focus Active Editor", "Terminal", "Escape", InputTargets.Terminal, () => workbench.PanelFocused = false);
	}

	private static void MoveQuickInput(Workbench workbench, int delta) {
		if (workbench.Palette.IsOpen) workbench.Palette.MoveSelection(delta);
		else workbench.QuickOpen.MoveSelection(delta);
	}

	private static void AcceptQuickInput(ShellContext context) {
		Workbench workbench = context.Workbench;

		if (workbench.Palette.IsOpen) {
			ICommand? picked = workbench.Palette.Accept();
			workbench.Palette.Hide();
			picked?.Run(context.Host);
			return;
		}

		if (workbench.QuickOpen.Accept() is { } path) workbench.OpenFile(path);

		workbench.QuickOpen.Hide();
	}
}
