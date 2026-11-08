namespace Forge.Common.Render;

public interface IFontSet
{
	ITextFont GetCode();
	ITextFont GetCodeStrong();
	ITextFont GetCodeSmall();
	ITextFont GetCodeLens();

	ITextFont GetUi();
	ITextFont GetUiStrong();
	ITextFont GetUiSmall();
	ITextFont GetUiDescription();

	ITextFont GetUi(float size, bool strong = false);
	ITextFont GetUiItalic(float size) => GetUi(size);
	ITextFont GetMono(float size, bool strong = false);
	ITextFont GetFamily(string[] families, float size);

	float GetScale();

	bool Rescale(float scale);
}
