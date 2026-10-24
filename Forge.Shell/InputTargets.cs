using Forge.Core.Native;
using Forge.Terminal;
using Forge.UI;

namespace Forge.Shell;

public static class InputTargets
{
	public const string ProcessExplorer = "processExplorerFocus";
	public const string Menu = "menu";
	public const string GitMenu = "gitMenu";
	public const string QuickInput = "inQuickInput";
	public const string FindWidget = "findWidgetVisible";
	public const string ScmInput = "scmInputFocused";
	public const string Terminal = "terminalFocus";
	public const string Output = "outputFocus";
	public const string Editor = "editorTextFocus";

	public static void Register(ShellContext context) {
		Workbench workbench = context.Workbench;

		context.AddTarget(new(ProcessExplorer, () => workbench.ProcessExplorer?.IsOpen == true));
		context.AddTarget(new(Menu, () => workbench.Menu.IsOpen));
		context.AddTarget(new(GitMenu, () => workbench.Sidebar.SourceControl.Menu.IsOpen));

		context.AddTarget(new(QuickInput, () => workbench.Palette.IsOpen || workbench.QuickOpen.IsOpen,
			character => {
				if (workbench.Palette.IsOpen) {
					workbench.Palette.Append(character);
					context.Damage(workbench.Palette.Bounds);
				}
				else {
					workbench.QuickOpen.Append(character);
					context.Damage(workbench.QuickOpen.Bounds);
				}
			},
			() => {
				if (workbench.Palette.IsOpen) workbench.Palette.Backspace();
				else workbench.QuickOpen.Backspace();

				context.Refresh();
			}));

		context.AddTarget(new(FindWidget, () => workbench.Find.IsOpen,
			character => {
				workbench.Find.Append(character);
				context.ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
				context.Refresh();
			},
			() => {
				workbench.Find.Backspace();
				context.ScrollToMatch(workbench.Find.Restart(workbench.ActiveView));
				context.Refresh();
			}));

		context.AddTarget(new(ScmInput, () => workbench.Sidebar.View == ActivityView.SourceControl && workbench.Sidebar.SourceControl.MessageFocused,
			character => {
				workbench.Sidebar.SourceControl.Append(character);
				context.Refresh();
			},
			() => {
				workbench.Sidebar.SourceControl.Backspace();
				context.Refresh();
			}));

		context.AddTarget(new(Terminal, () => workbench.PanelFocused && workbench.Panel.Active == PanelView.Terminal && workbench.Panel.Terminal is not null,
			character => workbench.Panel.Terminal?.Write([character]),
			Key: (key, control) => SendToTerminal(workbench, key, control)));

		context.AddTarget(new(Output, () => workbench.PanelFocused && workbench.Panel.Active == PanelView.Output,
			Key: (key, control) => HandleOutputKey(context, key, control)));

		context.AddTarget(new(Editor, () => workbench.ActiveView is not null,
			character => {
				if (workbench.ActiveView is not { } view) return;

				if (!view.TryAutoClose(character)) view.InsertText([character]);

				workbench.IntelliSense?.Typed(character);
				context.RevealCaret();
			}));
	}

	private static bool SendToTerminal(Workbench workbench, uint key, bool control) {
		if (workbench.Panel.Terminal is not { } terminal) return false;

		if (control && key is >= VirtualKey.A and <= VirtualKey.Z) {
			terminal.SendControl((char)key);
			return true;
		}

		if (key == VirtualKey.Right && !control && terminal.AcceptSuggestion()) return true;

		TerminalKey mapped = key switch {
			VirtualKey.Return => TerminalKey.Enter,
			VirtualKey.Back => TerminalKey.Backspace,
			VirtualKey.Up => TerminalKey.Up,
			VirtualKey.Down => TerminalKey.Down,
			VirtualKey.Left => TerminalKey.Left,
			VirtualKey.Right => TerminalKey.Right,
			VirtualKey.Home => TerminalKey.Home,
			VirtualKey.End => TerminalKey.End,
			VirtualKey.Delete => TerminalKey.Delete,
			VirtualKey.PageUp => TerminalKey.PageUp,
			VirtualKey.PageDown => TerminalKey.PageDown,
			_ => TerminalKey.None,
		};

		return terminal.SendKey(mapped, control);
	}

	private static bool HandleOutputKey(ShellContext context, uint key, bool control) {
		if (!control) return false;

		if (key == VirtualKey.C) {
			Clipboard.SetText(context.Workbench.Panel.CopyOutput());
			return true;
		}

		if (key != VirtualKey.A) return false;

		context.Workbench.Panel.SelectAllOutput();
		context.Refresh();
		return true;
	}
}
