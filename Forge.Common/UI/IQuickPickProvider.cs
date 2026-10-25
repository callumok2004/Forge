using Forge.Common.Render;

namespace Forge.Common.UI;

public interface IQuickPickProvider
{
	string GetPlaceholder();
	string GetEmptyMessage();

	int GetCount();
	void Filter(ReadOnlySpan<char> query);
	void PaintItem(IPainter painter, int index, float left, float top, float width, float height, bool selected);
	void Accept(int index);

	void Cancel() { }
}
