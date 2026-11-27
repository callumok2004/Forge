using Forge.Common.Render;
using Forge.Common.UI;

namespace Forge.Tests;

internal sealed class FakeFont(float size) : ITextFont
{
	public string GetFamilyName() => "Fake";
	public float GetSizeInPixels() => size;
	public float GetCellWidth() => size * 0.6f;
	public float GetLineHeight() => size * 1.3f;
	public float GetBaseline() => size;
	public float GetAscent() => size;
	public float GetDescent() => size * 0.3f;
	public bool IsMonospace() => true;
	public ushort GetGlyphIndex(char character) => 1;
	public float GetAdvance(char character) => size * 0.6f;
	public float Measure(ReadOnlySpan<char> text) => text.Length * size * 0.6f;
}

internal sealed class FakeFonts : IFontSet
{
	private static readonly FakeFont Font = new(13f);
	public ITextFont GetCode() => Font;
	public ITextFont GetCodeStrong() => Font;
	public ITextFont GetCodeSmall() => Font;
	public ITextFont GetCodeLens() => Font;
	public ITextFont GetUi() => Font;
	public ITextFont GetUiStrong() => Font;
	public ITextFont GetUiSmall() => Font;
	public ITextFont GetUiDescription() => Font;
	public ITextFont GetUi(float size, bool strong = false) => new FakeFont(size);
	public ITextFont GetMono(float size, bool strong = false) => new FakeFont(size);
	public ITextFont GetFamily(string[] families, float size) => new FakeFont(size);
	public float GetScale() => 1f;
	public bool Rescale(float scale) => false;
}
