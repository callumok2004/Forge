using Forge.Common.Commands;
using Forge.Core.Native;
using Forge.Editor;

namespace Forge.Shell.Actions;

public static class EditorCommands
{
	private const string Category = "Editor";

	public static void Register(ICommandRegistry registry, ShellContext context) {
		static bool Focused() => ShellContext.EditorFocused;

		void Add(string id, string title, string? keybinding, Action<TextView> action, bool navigates = false, bool deletes = false) =>
			registry.Register(new Command(id, title, Category, _ => {
				if (context.ActiveView is not { } view) return;

				if (navigates) view.PushUndoStop();

				action(view);

				if (deletes) context.Workbench.IntelliSense?.Deleted();

				if (navigates) context.Workbench.IntelliSense?.CursorMoved();

				context.RevealCaret();
			}, keybinding, Focused));

		void Move(string id, string title, string keybinding, Action<TextView, bool> move) {
			Add(id, title, keybinding, view => move(view, false), navigates: true);
			Add(id + "Select", title + " Select", "Shift+" + keybinding, view => move(view, true), navigates: true);
		}

		int Page() => Math.Max(1, (int)(context.Workbench.EditorHeight(context.Device.Height) / context.Painter.GetCode().GetLineHeight()) - 2);

		Move("cursorLeft", "Cursor Left", "Left", static (view, extend) => view.MoveHorizontal(-1, extend));
		Move("cursorRight", "Cursor Right", "Right", static (view, extend) => view.MoveHorizontal(1, extend));
		Move("cursorWordLeft", "Cursor Word Left", "Ctrl+Left", static (view, extend) => view.MoveWord(-1, extend));
		Move("cursorWordRight", "Cursor Word Right", "Ctrl+Right", static (view, extend) => view.MoveWord(1, extend));
		Move("cursorUp", "Cursor Up", "Up", static (view, extend) => view.MoveVertical(-1, extend));
		Move("cursorDown", "Cursor Down", "Down", static (view, extend) => view.MoveVertical(1, extend));
		Move("cursorPageUp", "Cursor Page Up", "PageUp", (view, extend) => view.MoveVertical(-Page(), extend));
		Move("cursorPageDown", "Cursor Page Down", "PageDown", (view, extend) => view.MoveVertical(Page(), extend));
		Move("cursorTop", "Cursor Top", "Ctrl+Home", static (view, extend) => view.MoveToDocumentStart(extend));
		Move("cursorBottom", "Cursor Bottom", "Ctrl+End", static (view, extend) => view.MoveToDocumentEnd(extend));
		Move("cursorHome", "Cursor Home", "Home", static (view, extend) => view.MoveToLineStart(extend));
		Move("cursorEnd", "Cursor End", "End", static (view, extend) => view.MoveToLineEnd(extend));

		Add("deleteLeft", "Delete Left", "Backspace", static view => view.Backspace(), deletes: true);
		Add("deleteRight", "Delete Right", "Delete", static view => view.DeleteForward(), deletes: true);
		Add("deleteWordLeft", "Delete Word Left", "Ctrl+Backspace", static view => view.DeleteWord(-1), deletes: true);
		Add("deleteWordRight", "Delete Word Right", "Ctrl+Delete", static view => view.DeleteWord(1), deletes: true);

		Add("editor.action.insertNewLine", "Insert New Line", "Enter", view => {
			view.InsertNewLine();
			context.Workbench.IntelliSense?.Typed('\n');
		});
		Add("editor.action.rename", "Rename Symbol", "F2", _ => context.Workbench.IntelliSense?.StartRename());
		Add("editor.action.quickFix", "Quick Fix...", "Ctrl+.", _ => context.Workbench.IntelliSense?.ShowCodeActions());
		Add("editor.action.revealDefinition", "Go to Definition", "F12", _ => context.Workbench.IntelliSense?.RevealDefinition());
		Add("editor.action.goToImplementation", "Go to Implementations", "Ctrl+F12", _ => context.Workbench.IntelliSense?.GoToImplementation());
		Add("editor.action.goToReferences", "Go to References", "Shift+F12", _ => context.Workbench.IntelliSense?.FindReferences());
		Add("editor.action.insertLineAfter", "Insert Line Below", "Ctrl+Enter", static view => view.InsertLine(1));
		Add("editor.action.insertLineBefore", "Insert Line Above", "Ctrl+Shift+Enter", static view => view.InsertLine(-1));

		Add("tab", "Tab", "Tab", static view => {
			if (view.HasSelection) view.IndentLines(1);
			else view.InsertTab();
		});

		Add("outdent", "Outdent", "Shift+Tab", static view => view.IndentLines(-1));
		Add("editor.action.indentLines", "Indent Line", "Ctrl+]", static view => view.IndentLines(1));
		Add("editor.action.outdentLines", "Outdent Line", "Ctrl+[", static view => view.IndentLines(-1));
		Add("editor.action.deleteLines", "Delete Line", "Ctrl+Shift+K", static view => view.DeleteLines());
		Add("editor.action.commentLine", "Toggle Line Comment", "Ctrl+/", static view => view.ToggleLineComment());
		Add("expandLineSelection", "Expand Line Selection", "Ctrl+L", static view => view.SelectLine());
		Add("editor.action.copyLinesUpAction", "Copy Line Up", "Shift+Alt+Up", static view => view.CopyLines(-1));
		Add("editor.action.copyLinesDownAction", "Copy Line Down", "Shift+Alt+Down", static view => view.CopyLines(1));
		Add("editor.action.moveLinesUpAction", "Move Line Up", "Alt+Up", static view => view.MoveLines(-1));
		Add("editor.action.moveLinesDownAction", "Move Line Down", "Alt+Down", static view => view.MoveLines(1));
		Add("editor.action.selectAll", "Select All", "Ctrl+A", static view => view.SelectAll());
		Add("editor.action.addSelectionToNextFindMatch", "Add Selection to Next Find Match", "Ctrl+D", static view => view.SelectNextOccurrence());
		Add("editor.action.jumpToBracket", "Go to Bracket", "Ctrl+Shift+\\", static view => view.GoToMatchingBracket());

		Add("editor.action.clipboardCopyAction", "Copy", "Ctrl+C", static view => Clipboard.SetText(view.CopyText()));

		Add("editor.action.clipboardCutAction", "Cut", "Ctrl+X", static view => {
			Clipboard.SetText(view.CopyText());
			view.Cut();
		});

		Add("editor.action.clipboardPasteAction", "Paste", "Ctrl+V", static view => view.InsertText(Clipboard.GetText()));
		Add("undo", "Undo", "Ctrl+Z", static view => view.Undo());
		Add("redo", "Redo", "Ctrl+Y", static view => view.Redo());
	}
}
