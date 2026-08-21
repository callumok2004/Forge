using DWrite = Vortice.DirectWrite;

using Forge.Common.Render;

namespace Forge.Render;

public enum TextWeight
{
	Regular,
	SemiBold,
	Bold,
}

public sealed class TextFont : ITextFont, IDisposable
{
	private const int AsciiCount = 128;

	private readonly ushort[] _asciiGlyphs = new ushort[AsciiCount];
	private readonly float[] _asciiAdvances = new float[AsciiCount];
	private readonly Dictionary<char, (ushort Glyph, float Advance)> _extended = [];

	private readonly uint[] _codePointScratch = new uint[1];
	private readonly ushort[] _glyphScratch = new ushort[1];
	private readonly float _designScale;

	private DWrite.IDWriteFontCollection _collection;
	private DWrite.IDWriteFontFamily _family;
	private DWrite.IDWriteFont _font;
	private DWrite.IDWriteFontFace _fontFace;

	private bool _disposed;

	public TextFont(DWrite.IDWriteFactory factory, string[] familyCandidates, float sizeInPixels,
		TextWeight textWeight = TextWeight.Regular, float lineHeightOverride = 0f) {
		DWrite.FontWeight weight = textWeight switch {
			TextWeight.SemiBold => DWrite.FontWeight.SemiBold,
			TextWeight.Bold => DWrite.FontWeight.Bold,
			_ => DWrite.FontWeight.Normal,
		};

		SizeInPixels = sizeInPixels;

		_collection = factory.GetSystemFontCollection(checkForUpdates: false);

		uint familyIndex = 0;
		FamilyName = familyCandidates.Length > 0 ? familyCandidates[0] : "Segoe UI";

		foreach (string candidate in familyCandidates) {
			if (_collection.FindFamilyName(candidate, out familyIndex)) {
				FamilyName = candidate;
				break;
			}
		}

		_family = _collection.GetFontFamily(familyIndex);
		_font = _family.GetFirstMatchingFont(weight, DWrite.FontStretch.Normal, DWrite.FontStyle.Normal);
		_fontFace = _font.CreateFontFace();

		DWrite.FontMetrics metrics = _fontFace.Metrics;
		_designScale = sizeInPixels / metrics.DesignUnitsPerEm;

		Ascent = metrics.Ascent * _designScale;
		Descent = metrics.Descent * _designScale;

		LineHeight = lineHeightOverride > 0f
			? MathF.Round(lineHeightOverride)
			: MathF.Round((metrics.Ascent + metrics.Descent + metrics.LineGap) * _designScale);

		float leading = LineHeight - (Ascent + Descent);
		Baseline = MathF.Round(Ascent + (leading / 2f));

		ResolveAscii();

		CellWidth = _asciiAdvances['0'];
		_isMonospace = DetectMonospace();
	}

	public string FamilyName { get; }

	public float SizeInPixels { get; }

	public float CellWidth { get; }

	private readonly bool _isMonospace;

	public float LineHeight { get; }

	public float Baseline { get; }

	public string GetFamilyName() => FamilyName;

	public float GetSizeInPixels() => SizeInPixels;

	public float GetCellWidth() => CellWidth;

	public float GetLineHeight() => LineHeight;

	public float GetBaseline() => Baseline;

	public float GetAscent() => Ascent;

	public float GetDescent() => Descent;

	public bool IsMonospace() => _isMonospace;

	public float Ascent { get; }

	public float Descent { get; }

	internal DWrite.IDWriteFontFace FontFace => _fontFace;

	private void ResolveAscii() {
		uint[] codePoints = new uint[AsciiCount];
		for (int i = 0; i < AsciiCount; i++) codePoints[i] = (uint)i;

		ushort[] indices = new ushort[AsciiCount];
		_fontFace.GetGlyphIndices(codePoints, indices);

		DWrite.GlyphMetrics[] metrics = _fontFace.GetDesignGlyphMetrics(indices, isSideways: false);

		for (int i = 0; i < AsciiCount; i++) {
			_asciiGlyphs[i] = indices[i];
			_asciiAdvances[i] = metrics[i].AdvanceWidth * _designScale;
		}
	}

	private bool DetectMonospace() {
		float reference = _asciiAdvances['0'];
		for (char c = '!'; c <= '~'; c++) {
			if (MathF.Abs(_asciiAdvances[c] - reference) > 0.01f) return false;
		}

		return true;
	}

	public ushort GetGlyphIndex(char character) {
		if (character < AsciiCount) return _asciiGlyphs[character];

		return Resolve(character).Glyph;
	}

	public float GetAdvance(char character) {
		if (character < AsciiCount) return _asciiAdvances[character];

		return Resolve(character).Advance;
	}

	private (ushort Glyph, float Advance) Resolve(char character) {
		if (_extended.TryGetValue(character, out (ushort Glyph, float Advance) cached)) return cached;

		(ushort Glyph, float Advance) resolved = (0, CellWidth);

		try {
			_codePointScratch[0] = character;
			_fontFace.GetGlyphIndices(_codePointScratch, _glyphScratch);

			ushort glyph = _glyphScratch[0];
			DWrite.GlyphMetrics[] metrics = _fontFace.GetDesignGlyphMetrics([glyph], isSideways: false);
			resolved = (glyph, metrics[0].AdvanceWidth * _designScale);
		}
		catch (Exception) {
		}

		_extended[character] = resolved;
		return resolved;
	}

	public float Measure(ReadOnlySpan<char> text) {
		if (_isMonospace) return text.Length * CellWidth;

		float width = 0f;
		for (int i = 0; i < text.Length; i++) width += GetAdvance(text[i]);

		return width;
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		_fontFace?.Dispose();
		_font?.Dispose();
		_family?.Dispose();
		_collection?.Dispose();

		_fontFace = null!;
		_font = null!;
		_family = null!;
		_collection = null!;
	}
}
