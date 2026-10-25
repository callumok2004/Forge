using Forge.Common.Commands;

namespace Forge.Common.Editor;

public interface IEditorHost
{
	string? GetActiveFilePath();
	IDocument? GetActiveDocument();
	ITextView? GetActiveView();

	void PublishActiveDocument();

	bool ApplyWorkspaceEdit(WorkspaceEdit edit);

	void RestoreEditorFocus();

	void GoTo(string path, int line, int column);

	void ShowReferences(string title, IReadOnlyList<HostLocation> locations);

	void PeekReferences(string title, int line, int column, IReadOnlyList<HostLocation> locations);
}
