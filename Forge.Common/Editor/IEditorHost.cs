namespace Forge.Common.Editor;

public interface IEditorHost
{
	string? GetActiveFilePath();
	IDocument? GetActiveDocument();
	ITextView? GetActiveView();

	void PublishActiveDocument();
}
