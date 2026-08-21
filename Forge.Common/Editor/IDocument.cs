using Forge.Text;

namespace Forge.Common.Editor;

public interface IDocument
{
	string? GetPath();
	string GetName();

	TextBuffer GetBuffer();

	bool IsDirty();

	bool Save();
	bool SaveAs(string path);
}
