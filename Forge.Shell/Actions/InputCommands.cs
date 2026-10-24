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

		Add("closeFindWidget", "Close Find Widget", "Editor", "Escape", InputTargets.FindWidget, workbench.Find.Hide);
		Add("editor.action.nextMatchFindAction", "Find Next", "Editor", "Enter|F3", InputTargets.FindWidget, () => context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, 1)));
		Add("editor.action.previousMatchFindAction", "Find Previous", "Editor", "Shift+Enter|Shift+F3", InputTargets.FindWidget, () => context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, -1)));

		Add("toggleFindCaseSensitive", "Toggle Match Case", "Editor", "Alt+C", InputTargets.FindWidget, () => {
			workbench.Find.ToggleMatchCase();
			context.ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
		});

		Add("forge.gitMenu.close", "Close Source Control Menu", "View", "Escape", InputTargets.GitMenu, workbench.Sidebar.SourceControl.Menu.Close);

		Add("scm.acceptInput", "Commit", "Source Control", "Ctrl+Enter", InputTargets.ScmInput, () => SourceControlCommands.HandleCommit(workbench, context.Refresh));
		Add("forge.scm.insertNewLine", "Insert New Line", "Source Control", "Enter", InputTargets.ScmInput, () => workbench.Sidebar.SourceControl.Append('\n'));
		Add("forge.scm.blurInput", "Leave Commit Message", "Source Control", "Escape", InputTargets.ScmInput, () => workbench.Sidebar.SourceControl.FocusMessage(false));

		Add("workbench.action.terminal.focusEditor", "Focus Active Editor", "Terminal", "Escape", InputTargets.Terminal, () => workbench.PanelFocused = false);
	}
}
