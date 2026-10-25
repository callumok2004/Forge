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

	float GetScale();

	bool Rescale(float scale);
}
