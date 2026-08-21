namespace Forge.Common.Render;

public interface IFontSet
{
	ITextFont GetCode();
	ITextFont GetCodeStrong();
	ITextFont GetCodeSmall();

	ITextFont GetUi();
	ITextFont GetUiStrong();
	ITextFont GetUiSmall();

	float GetScale();

	bool Rescale(float scale);
}
