using Forge.Common.Render;
using Forge.Text;

namespace Forge.Common.Editor;

public interface ITextView
{
	TextSnapshot GetSnapshot();

	int GetCaretOffset();
	int GetCaretLine();
	int GetCaretColumn();

	int GetSelectionLength();

	int GetTabWidth();

	LineEnding GetLineEnding();

	string GetLanguageName();

	int VisualColumnAt(int line, int offset);

	void MoveTo(int offset, bool extend);
	void ExternalEdit(int changedOffset, int delta);

	(float X, float Y) PointForOffset(ITextFont font, int offset, float viewportX, float viewportY);
}
