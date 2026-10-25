using Forge.Common.Commands;
using Forge.Common.UI;

namespace Forge.Shell.Actions;

public static class InputCommands
{
	public static void Register(ICommandRegistry registry, ShellContext context) {
		Workbench workbench = context.Workbench;

		void Add(string id, string title, string category, string keybinding, Func<bool> when, Action action) => registry.Register(new Command(id, title, category, _ => {
			action();
			context.Refresh();
		}, keybinding, when));

		bool FindOpen() => workbench.Find.IsOpen;

		Add("closeFindWidget", "Close Find Widget", "Editor", "Escape", FindOpen, workbench.Find.Hide);
		Add("editor.action.nextMatchFindAction", "Find Next", "Editor", "Enter|F3", FindOpen, () => context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, 1)));
		Add("editor.action.previousMatchFindAction", "Find Previous", "Editor", "Shift+Enter|Shift+F3", FindOpen, () => context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, -1)));

		Add("toggleFindCaseSensitive", "Toggle Match Case", "Editor", "Alt+C", FindOpen, workbench.Find.ToggleMatchCase);

		Add("workbench.action.terminal.focusEditor", "Focus Active Editor", "Terminal", "Escape", static () => ShellContext.IsFocused(ContextKeys.TerminalFocus), () => workbench.PanelFocused = false);
	}
}
