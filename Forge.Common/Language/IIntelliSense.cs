using Forge.Common.Commands;
using Forge.Common.Render;

namespace Forge.Common.Language;

public interface IIntelliSense
{
	ILanguageFeatures? GetFeatures();
	void SetFeatures(ILanguageFeatures features);

	void Typed(char character);
	void Deleted();
	void Dismiss();

	void TriggerSuggest();
	bool HandleKey(uint key, bool control);
	void Accept();

	void Tick();
	void PointerMoved(int line, int column, float x, float y);
	void PointerLeft();

	void Render(IPainter painter, float editorLeft, float editorTop, float editorBottom, float editorRight, float scale);
}
