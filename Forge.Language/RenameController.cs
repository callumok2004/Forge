using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Text;

namespace Forge.Language;

public sealed class RenameController(IEditorHost workbench, UiQueue ui, EditorMessageWidget message)
{
	public RenameWidget Widget { get; } = new();

	public void Start(ILanguageFeatures features) {
		if (workbench.GetActiveFilePath() is not { } path || workbench.GetActiveView() is not { } view) return;

		workbench.PublishActiveDocument();

		int line = view.GetCaretLine() + 1;
		int column = view.GetCaretColumn() + 1;
		int offset = view.GetCaretOffset();

		_ = Task.Run(async () => {
			RenameTarget? target = await features.PrepareRenameAsync(path, line, column).ConfigureAwait(false);

			ui.Post(() => {
				if (target is not { } found) message.Show("The element can't be renamed.", offset);
				else Show(features, path, line, column, offset, found);
			});
		});
	}

	private void Show(ILanguageFeatures features, string path, int line, int column, int offset, RenameTarget target) {
		if (workbench.GetActiveView() is not { } view || workbench.GetActiveFilePath() != path) return;

		TextSnapshot snapshot = view.GetSnapshot();

		if (target.Line < 1 || target.Line > snapshot.LineCount) return;

		TextSpan span = snapshot.GetLineSpan(target.Line - 1);
		int start = Math.Clamp(span.Start + target.StartColumn - 1, span.Start, span.End);
		int end = Math.Clamp(span.Start + target.EndColumn - 1, start, span.End);
		string name = target.Placeholder.Length > 0 ? target.Placeholder : snapshot.GetText(start, end - start);

		Widget.Accepted = newName => Commit(features, path, line, column, offset, name, newName);
		Widget.Closed = workbench.RestoreEditorFocus;
		Widget.Show(target.Line, target.StartColumn, name);
	}

	private void Commit(ILanguageFeatures features, string path, int line, int column, int offset, string oldName, string newName) {
		if (newName == oldName) return;

		_ = Task.Run(async () => {
			WorkspaceEdit? edit = await features.RenameAsync(path, line, column, newName).ConfigureAwait(false);

			ui.Post(() => {
				if (edit is { IsEmpty: false }) workbench.ApplyWorkspaceEdit(edit);
				else message.Show("Rename failed to compute edits.", offset);
			});
		});
	}
}
