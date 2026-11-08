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
		static bool FindInput() => ShellContext.IsFocused(ContextKeys.FindInputFocused);
		static bool ReplaceInput() => ShellContext.IsFocused(ContextKeys.ReplaceInputFocused);

		Add("editor.action.nextMatchFindAction", "Find Next", "Editor", "Enter", FindInput, () => context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, 1)));
		Add("editor.action.previousMatchFindAction", "Find Previous", "Editor", "Shift+Enter", FindInput, () => context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, -1)));
		Add("editor.action.nextMatchFindActionF3", "Find Next", "Editor", "F3", static () => true, () => {
			if (!workbench.Find.IsOpen) workbench.Find.Show(workbench.ActiveView);

			context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, 1));
		});
		Add("editor.action.previousMatchFindActionF3", "Find Previous", "Editor", "Shift+F3", static () => true, () => {
			if (!workbench.Find.IsOpen) workbench.Find.Show(workbench.ActiveView);

			context.ScrollToMatch(workbench.Find.Step(workbench.ActiveView, -1));
		});
		Add("editor.action.replaceOne", "Replace", "Editor", "Enter|Ctrl+Shift+1", ReplaceInput, workbench.Find.ReplaceOne);
		Add("editor.action.replaceAll", "Replace All", "Editor", "Ctrl+Alt+Enter", FindOpen, workbench.Find.ReplaceAll);

		Add("toggleFindCaseSensitive", "Toggle Match Case", "Editor", "Alt+C", FindOpen, workbench.Find.ToggleMatchCase);
		Add("toggleFindWholeWord", "Toggle Match Whole Word", "Editor", "Alt+W", FindOpen, workbench.Find.ToggleWholeWord);
		Add("toggleFindRegex", "Toggle Use Regular Expression", "Editor", "Alt+R", FindOpen, workbench.Find.ToggleRegex);
		Add("togglePreserveCase", "Toggle Preserve Case", "Editor", "Alt+P", FindOpen, workbench.Find.TogglePreserveCase);
		Add("toggleFindInSelection", "Toggle Find in Selection", "Editor", "Alt+L", FindOpen, workbench.Find.ToggleFindInSelection);

		Add("workbench.action.terminal.focusEditor", "Focus Active Editor", "Terminal", "Escape", static () => ShellContext.IsFocused(ContextKeys.TerminalFocus), () => workbench.PanelFocused = false);
	}
}
