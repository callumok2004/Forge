using D2D = Vortice.Direct2D1;
using DWrite = Vortice.DirectWrite;
using System.Numerics;

using Forge.Common.Render;
using Forge.Core;
using Forge.Theming;

namespace Forge.Render;

public sealed class Painter : IPainter, IDisposable
{
	private readonly D2D.ID2D1DeviceContext _context;
	private readonly D2D.ID2D1Factory1 _factory;
	private readonly D2D.ID2D1SolidColorBrush _brush;

	private readonly DWrite.GlyphRun _textRun = new() { IsSideways = false, BidiLevel = 0 };
	private readonly DWrite.GlyphRun _iconRun = new() { Indices = new ushort[1], Advances = new float[1], IsSideways = false, BidiLevel = 0 };

	private ushort[] _glyphIndices = new ushort[256];
	private float[] _glyphAdvances = new float[256];

	private readonly ushort[]?[] _indexCache = new ushort[257][];
	private readonly float[]?[] _advanceCache = new float[257][];

	private bool _disposed;

	private readonly FontSet _fonts;

	public Painter(RenderDevice device, FontSet fonts) {
		_context = device.Context;
		_factory = device.Factory;
		_fonts = fonts;

		_brush = _context.CreateSolidColorBrush(new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f));

		_context.TextAntialiasMode = D2D.TextAntialiasMode.Cleartype;
	}

	public PixelRect Damage { get; private set; }

	public void BeginFrame(PixelRect damage) => Damage = damage;

	public bool NeedsPaint(float x, float y, float width, float height) =>
		Damage.Intersects(x, y, width, height);

	public TextFont Code => _fonts.Code;

	public TextFont CodeStrong => _fonts.CodeStrong;

	public TextFont CodeSmall => _fonts.CodeSmall;

	public TextFont Ui => _fonts.Ui;

	public TextFont UiStrong => _fonts.UiStrong;

	public TextFont UiSmall => _fonts.UiSmall;

	public IconFont Icons => _fonts.Icons;

	public FileIconSet? FileIcons { get; set; }

	public PixelRect GetDamage() => Damage;

	ITextFont IPainter.GetCode() => Code;

	ITextFont IPainter.GetCodeStrong() => CodeStrong;

	ITextFont IPainter.GetCodeSmall() => CodeSmall;

	ITextFont IPainter.GetUi() => Ui;

	ITextFont IPainter.GetUiStrong() => UiStrong;

	ITextFont IPainter.GetUiSmall() => UiSmall;

	void IPainter.DrawVectorIcon(IVectorIcon icon, float centreX, float centreY, float size, Color? tint) =>
		DrawVectorIcon((VectorIcon)icon, centreX, centreY, size, tint);

	public bool DrawFileIcon(string fileName, float centreX, float centreY, float size) {
		if (FileIcons?.ForFile(fileName) is not { } icon) return false;

		DrawVectorIcon(icon, centreX, centreY, size);
		return true;
	}

	public bool DrawFolderIcon(string folderName, bool expanded, float centreX, float centreY, float size) {
		if (FileIcons?.ForFolder(folderName, expanded) is not { } icon) return false;

		DrawVectorIcon(icon, centreX, centreY, size);
		return true;
	}

	void IPainter.DrawImage(IImage image, float centreX, float centreY, float size) =>
		DrawImage((Image)image, centreX, centreY, size);

	void IPainter.DrawText(ITextFont font, ReadOnlySpan<char> text, float x, float baselineY, Color color) =>
		DrawText((TextFont)font, text, x, baselineY, color);

	public void DrawVectorIcon(VectorIcon icon, float centreX, float centreY, float size,
		Color? tint = null) =>
		icon.Draw(_context, _brush, centreX, centreY, size, tint);

	public void DrawIcon(Codicon icon, float centreX, float centreY, float size, Color color) {
		if (!Icons.TryPlace(icon, centreX, centreY, size, out float emSize, out float originX,
			out float originY, out ushort glyph)) return;

		_brush.Color = ToColor4(color);

		_iconRun.FontFace = Icons.FontFace;
		_iconRun.FontEmSize = emSize;
		_iconRun.Indices![0] = glyph;

		_context.DrawGlyphRun(new Vector2(originX, originY), _iconRun, _brush, Vortice.DCommon.MeasuringMode.Natural);
	}

	public void FillRectangle(float x, float y, float width, float height, Color color) {
		_brush.Color = ToColor4(color);
		_context.FillRectangle(new Vortice.Mathematics.Rect(x, y, width, height), _brush);
	}

	public void DrawVerticalLine(float x, float top, float bottom, Color color) =>
		FillRectangle(MathF.Round(x), top, 1f, bottom - top, color);

	public void DrawHorizontalLine(float y, float left, float right, Color color) =>
		FillRectangle(left, MathF.Round(y), right - left, 1f, color);

	public void FillRoundedRectangle(float x, float y, float width, float height, float radius, Color color) {
		_brush.Color = ToColor4(color);
		_context.FillRoundedRectangle(
			new D2D.RoundedRectangle {
				Rect = new Vortice.Mathematics.Rect(x, y, width, height),
				RadiusX = radius,
				RadiusY = radius,
			},
			_brush);
	}

	public void StrokeRoundedRectangle(float x, float y, float width, float height, float radius,
		float thickness, Color color) {
		_brush.Color = ToColor4(color);
		_context.DrawRoundedRectangle(
			new D2D.RoundedRectangle {
				Rect = new Vortice.Mathematics.Rect(x, y, width, height),
				RadiusX = radius,
				RadiusY = radius,
			},
			_brush,
			thickness);
	}

	public void FillCircle(float centreX, float centreY, float radius, Color color) {
		_brush.Color = ToColor4(color);
		_context.FillEllipse(
			new D2D.Ellipse(new Vector2(centreX, centreY), radius, radius),
			_brush);
	}

	public void StrokeLine(float x0, float y0, float x1, float y1, float thickness, Color color) {
		_brush.Color = ToColor4(color);
		_context.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), _brush, thickness);
	}

	public void DrawChevron(float centreX, float centreY, float size, bool pointingDown, Color color) {
		float half = size / 2f;

		if (pointingDown) {
			StrokeLine(centreX - half, centreY - (half / 2f), centreX, centreY + (half / 2f), 1.5f, color);
			StrokeLine(centreX, centreY + (half / 2f), centreX + half, centreY - (half / 2f), 1.5f, color);
		}
		else {
			StrokeLine(centreX - (half / 2f), centreY - half, centreX + (half / 2f), centreY, 1.5f, color);
			StrokeLine(centreX + (half / 2f), centreY, centreX - (half / 2f), centreY + half, 1.5f, color);
		}
	}

	public void DrawCross(float centreX, float centreY, float size, Color color) {
		float half = size / 2f;
		StrokeLine(centreX - half, centreY - half, centreX + half, centreY + half, 1.4f, color);
		StrokeLine(centreX + half, centreY - half, centreX - half, centreY + half, 1.4f, color);
	}

	private static Vortice.Mathematics.Color4 ToColor4(Color color) => new(color.R, color.G, color.B, color.A);

	public void DrawText(TextFont font, ReadOnlySpan<char> text, float x, float baselineY, Color color) {
		if (text.IsEmpty) return;

		EnsureGlyphCapacity(text.Length);

		bool monospace = font.IsMonospace();
		float cellWidth = font.CellWidth;

		int count = 0;
		for (int i = 0; i < text.Length; i++) {
			char character = text[i];

			if (character < ' ') continue;

			_glyphIndices[count] = font.GetGlyphIndex(character);
			_glyphAdvances[count] = monospace ? cellWidth : font.GetAdvance(character);
			count++;
		}

		if (count == 0) return;

		_brush.Color = ToColor4(color);

		_textRun.FontFace = font.FontFace;
		_textRun.FontEmSize = font.SizeInPixels;
		_textRun.Indices = ExactIndices(count);
		_textRun.Advances = ExactAdvances(count);

		_context.DrawGlyphRun(new Vector2(x, baselineY), _textRun, _brush, Vortice.DCommon.MeasuringMode.Natural);
	}

	public void EndFrame() {
	}

	public void FillTriangle(float x0, float y0, float x1, float y1, float x2, float y2, Color color) {
		_brush.Color = ToColor4(color);

		using D2D.ID2D1PathGeometry geometry = _factory.CreatePathGeometry();
		using (D2D.ID2D1GeometrySink sink = geometry.Open()) {
			sink.BeginFigure(new Vector2(x0, y0), D2D.FigureBegin.Filled);
			sink.AddLine(new Vector2(x1, y1));
			sink.AddLine(new Vector2(x2, y2));
			sink.EndFigure(D2D.FigureEnd.Closed);
			sink.Close();
		}

		_context.FillGeometry(geometry, _brush);
	}

	public void DrawImage(Image image, float centreX, float centreY, float size) {
		float half = size / 2f;

		_context.DrawBitmap(
			image.Bitmap,
			new Vortice.Mathematics.Rect(centreX - half, centreY - half, size, size),
			1f,
			D2D.BitmapInterpolationMode.Linear,
			new Vortice.Mathematics.Rect(0f, 0f, image.Width, image.Height));
	}

	private int _clipDepth;
	private int _layerDepth;

	public void PushClip(float x, float y, float width, float height) {
		_context.PushAxisAlignedClip(
			new Vortice.Mathematics.Rect(x, y, width, height),
			D2D.AntialiasMode.Aliased);

		_clipDepth++;
	}

	public void PopClip() {
		if (_clipDepth == 0) return;

		_context.PopAxisAlignedClip();
		_clipDepth--;
	}

	public void UnwindClips() {
		while (_layerDepth > 0) PopRoundedClip();

		while (_clipDepth > 0) PopClip();
	}

	public void PushRoundedClip(float x, float y, float width, float height, float radius) {
		using D2D.ID2D1RoundedRectangleGeometry geometry = _factory.CreateRoundedRectangleGeometry(
			new D2D.RoundedRectangle {
				Rect = new Vortice.Mathematics.Rect(x, y, width, height),
				RadiusX = radius,
				RadiusY = radius,
			});

		_context.PushLayer(
			new D2D.LayerParameters1 {
				ContentBounds = new Vortice.Mathematics.Rect(x, y, width, height),
				GeometricMask = geometry,
				MaskAntialiasMode = D2D.AntialiasMode.PerPrimitive,
				MaskTransform = Matrix3x2.Identity,
				Opacity = 1f,
			},
			null!);

		_layerDepth++;
	}

	public void PopRoundedClip() {
		if (_layerDepth == 0) return;

		_context.PopLayer();
		_layerDepth--;
	}

	private ushort[] ExactIndices(int count) {
		ushort[] cached = count < _indexCache.Length ? _indexCache[count] ??= new ushort[count] : new ushort[count];
		_glyphIndices.AsSpan(0, count).CopyTo(cached);
		return cached;
	}

	private float[] ExactAdvances(int count) {
		float[] cached = count < _advanceCache.Length ? _advanceCache[count] ??= new float[count] : new float[count];
		_glyphAdvances.AsSpan(0, count).CopyTo(cached);
		return cached;
	}

	private void EnsureGlyphCapacity(int required) {
		if (required <= _glyphIndices.Length) return;

		int capacity = _glyphIndices.Length;
		while (capacity < required) capacity *= 2;

		_glyphIndices = new ushort[capacity];
		_glyphAdvances = new float[capacity];
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;
		_textRun.FontFace = null;
		_iconRun.FontFace = null;
		_brush.Dispose();
	}
}
