namespace Forge.Common.Render;

public interface ITextFont
{
	string GetFamilyName();

	float GetSizeInPixels();
	float GetCellWidth();
	float GetLineHeight();
	float GetBaseline();
	float GetAscent();
	float GetDescent();

	bool IsMonospace();

	ushort GetGlyphIndex(char character);
	float GetAdvance(char character);
	float Measure(ReadOnlySpan<char> text);
}
