using Forge.Core;
using Forge.Theming;

namespace Forge.Common.Render;

public interface IVectorIcon
{
	Theming.Color GetDominantColor();
}

public interface IImage
{
	int GetWidth();
	int GetHeight();
}

public interface IPainter
{
	PixelRect GetDamage();
	void BeginFrame(PixelRect damage);
	void EndFrame();

	bool NeedsPaint(float x, float y, float width, float height);

	ITextFont GetCode();
	ITextFont GetCodeStrong();
	ITextFont GetCodeSmall();

	ITextFont GetCodeLens();
	ITextFont GetUi();
	ITextFont GetUiStrong();
	ITextFont GetUiSmall();

	ITextFont GetUiDescription();

	void FillRectangle(float x, float y, float width, float height, Color color);
	void FillRoundedRectangle(float x, float y, float width, float height, float radius, Color color);
	void StrokeRoundedRectangle(float x, float y, float width, float height, float radius, float thickness, Color color);
	void FillCircle(float centreX, float centreY, float radius, Color color);
	void FillTriangle(float x0, float y0, float x1, float y1, float x2, float y2, Color color);

	void DrawVerticalLine(float x, float top, float bottom, Color color);
	void DrawHorizontalLine(float y, float left, float right, Color color);
	void StrokeLine(float x0, float y0, float x1, float y1, float thickness, Color color);

	void DrawChevron(float centreX, float centreY, float size, bool pointingDown, Color color);
	void DrawCross(float centreX, float centreY, float size, Color color);

	void DrawIcon(Codicon icon, float centreX, float centreY, float size, Color color);
	void DrawVectorIcon(IVectorIcon icon, float centreX, float centreY, float size, Color? tint = null);
	void DrawImage(IImage image, float centreX, float centreY, float size);

	bool DrawFileIcon(ReadOnlySpan<char> fileName, float centreX, float centreY, float size);
	bool DrawFolderIcon(ReadOnlySpan<char> folderName, bool expanded, float centreX, float centreY, float size);

	void DrawText(ITextFont font, ReadOnlySpan<char> text, float x, float baselineY, Color color);

	void PushClip(float x, float y, float width, float height);

	void ClearRectangle(float x, float y, float width, float height);
	void PopClip();
	void UnwindClips();

	void PushRoundedClip(float x, float y, float width, float height, float radius);
	void PopRoundedClip();
}
