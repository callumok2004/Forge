using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Text;
using Forge.Workspace;

namespace Forge.Language;

public sealed class TypingAssist(IEditorHost workbench, UiQueue ui)
{
	public void Typed(ILanguageFeatures features, char character) {
		if (workbench.GetActiveFilePath() is not { } path || workbench.GetActiveDocument() is not { } document || workbench.GetActiveView() is not { } view) return;

		LanguageTriggers triggers = features.GetTriggers();
		bool autoInsert = triggers.AutoInsert.Contains(character);
		bool onType = triggers.OnTypeFormatting.Contains(character);

		if (!autoInsert && !onType) return;

		workbench.PublishActiveDocument();

		int version = document.GetBuffer().Version;
		int line = view.GetCaretLine() + 1;
		int column = view.GetCaretColumn() + 1;

		_ = Task.Run(async () => {
			if (autoInsert && await features.AutoInsertAsync(path, line, column, character).ConfigureAwait(false) is { } insert) {
				(string text, int caret) = insert.IsSnippet ? Snippet.Expand(insert.Edit.NewText) : (insert.Edit.NewText, -1);
				ui.Post(() => Apply(document, version, [insert.Edit with { NewText = text }], caret));
				return;
			}

			if (!onType) return;

			IReadOnlyList<HostTextEdit> edits = await features.OnTypeFormattingAsync(path, line, column, character).ConfigureAwait(false);

			if (edits.Count > 0) ui.Post(() => Apply(document, version, edits, -1));
		});
	}

	private void Apply(IDocument document, int version, IReadOnlyList<HostTextEdit> edits, int caretInEdit) {
		if (workbench.GetActiveDocument() != document || workbench.GetActiveView() is not { } view || document.GetBuffer().Version != version) return;

		DocumentEdits.Apply(document.GetBuffer(), edits, out int earliest, out int delta);

		if (earliest != int.MaxValue) view.ExternalEdit(earliest, delta);

		if (caretInEdit < 0 || edits.Count != 1) return;

		HostTextEdit edit = edits[0];
		TextSnapshot snapshot = view.GetSnapshot();

		if (edit.StartLine < 1 || edit.StartLine > snapshot.LineCount) return;

		TextSpan span = snapshot.GetLineSpan(edit.StartLine - 1);
		int start = Math.Clamp(span.Start + edit.StartColumn - 1, span.Start, span.End);

		view.MoveTo(Math.Min(start + caretInEdit, snapshot.Length), extend: false);
	}
}
