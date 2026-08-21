using DWrite = Vortice.DirectWrite;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class IconFont : IDisposable
{
	private readonly Dictionary<char, Metrics> _cache = [];
	private readonly uint[] _codePointScratch = new uint[1];
	private readonly ushort[] _glyphScratch = new ushort[1];

	private readonly ushort _designUnitsPerEm;
	private readonly float _designAscent;
	private readonly float _designDescent;

	private DWrite.IDWriteFontFile _file;
	private DWrite.IDWriteFontFace _fontFace;

	private bool _disposed;

	private readonly record struct Metrics(ushort Glyph, float DesignAdvance, float DesignWidth,
		float DesignLeft, float DesignTop, float DesignHeight);

	public IconFont(DWrite.IDWriteFactory factory, string filePath) {
		_file = factory.CreateFontFileReference(filePath);
		_fontFace = factory.CreateFontFace(DWrite.FontFaceType.Truetype, [_file], 0,
			DWrite.FontSimulations.None)
			?? throw new InvalidOperationException($"Could not load the icon font from '{filePath}'.");

		DWrite.FontMetrics metrics = _fontFace.Metrics;
		_designUnitsPerEm = metrics.DesignUnitsPerEm;
		_designAscent = metrics.Ascent;
		_designDescent = metrics.Descent;
	}

	internal DWrite.IDWriteFontFace FontFace => _fontFace;

	internal bool TryPlace(Codicon icon, float centreX, float centreY, float size, out float emSize,
		out float originX, out float originY, out ushort glyph) {
		Metrics metrics = Resolve((char)icon);

		glyph = metrics.Glyph;
		emSize = size;
		originX = 0f;
		originY = 0f;

		if (glyph == 0 || metrics.DesignHeight <= 0f) return false;

		float scale = size / _designUnitsPerEm;

		originX = MathF.Round(centreX - (metrics.DesignAdvance * scale / 2f));
		originY = MathF.Round(centreY + ((_designAscent - _designDescent) * scale / 2f));

		return true;
	}

	private Metrics Resolve(char codePoint) {
		if (_cache.TryGetValue(codePoint, out Metrics cached)) return cached;

		Metrics resolved = default;

		try {
			_codePointScratch[0] = codePoint;
			_fontFace.GetGlyphIndices(_codePointScratch, _glyphScratch);

			ushort glyph = _glyphScratch[0];
			DWrite.GlyphMetrics m = _fontFace.GetDesignGlyphMetrics([glyph], isSideways: false)[0];

			float width = m.AdvanceWidth - m.LeftSideBearing - m.RightSideBearing;
			float height = m.AdvanceHeight - m.TopSideBearing - m.BottomSideBearing;

			resolved = new Metrics(glyph, m.AdvanceWidth, width, m.LeftSideBearing,
				m.VerticalOriginY - m.TopSideBearing, height);
		}
		catch (Exception) {
		}

		_cache[codePoint] = resolved;
		return resolved;
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		_fontFace?.Dispose();
		_file?.Dispose();

		_fontFace = null!;
		_file = null!;
	}
}
