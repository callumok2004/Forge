using DWrite = Vortice.DirectWrite;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class FontSet : IFontSet, IDisposable
{
	private readonly DWrite.IDWriteFactory _factory;
	private string[] _codeFamily;
	private string[] _uiFamily;
	private float _codeSize;
	private float _uiSize;
	private readonly float _labelSize;
	private float _lineHeight;
	private readonly BundledFonts _bundled;

	private bool _disposed;

	public FontSet(DWrite.IDWriteFactory factory, string[] codeFamily, float codeSize, float lineHeight,
		string[] uiFamily, float uiSize, float labelSize, string iconFontPath, float scale) {
		_lineHeight = lineHeight;
		_factory = factory;
		_codeFamily = codeFamily;
		_uiFamily = uiFamily;
		_codeSize = codeSize;
		_uiSize = uiSize;
		_labelSize = labelSize;

		Icons = new IconFont(factory, iconFontPath);
		_bundled = new BundledFonts(factory, Path.GetDirectoryName(iconFontPath) ?? AppContext.BaseDirectory);

		Scale = scale;
		Build(scale);
	}

	public TextFont Code { get; private set; } = null!;

	public TextFont CodeStrong { get; private set; } = null!;

	public TextFont CodeSmall { get; private set; } = null!;

	public TextFont CodeLens { get; private set; } = null!;

	public TextFont Ui { get; private set; } = null!;

	public TextFont UiStrong { get; private set; } = null!;

	public TextFont UiSmall { get; private set; } = null!;

	public TextFont UiDescription { get; private set; } = null!;

	public IconFont Icons { get; }

	public float Scale { get; private set; }

	public ITextFont GetCode() => Code;

	public ITextFont GetCodeStrong() => CodeStrong;

	public ITextFont GetCodeSmall() => CodeSmall;

	public ITextFont GetCodeLens() => CodeLens;

	public ITextFont GetUi() => Ui;

	public ITextFont GetUiStrong() => UiStrong;

	public ITextFont GetUiSmall() => UiSmall;

	public ITextFont GetUiDescription() => UiDescription;

	public float GetScale() => Scale;

	private readonly Dictionary<(bool Mono, int Pixels, bool Strong), TextFont> _sized = [];

	public ITextFont GetUi(float size, bool strong = false) => Sized(false, size, strong);

	private readonly Dictionary<int, TextFont> _italic = [];

	public ITextFont GetUiItalic(float size) {
		int pixels = Math.Max(1, (int)MathF.Round(size * Scale));

		if (_italic.TryGetValue(pixels, out TextFont? font)) return font;

		font = new TextFont(_factory, _bundled.Collection, _uiFamily, pixels, TextWeight.Regular, 0f, italic: true);
		_italic[pixels] = font;
		return font;
	}

	public ITextFont GetMono(float size, bool strong = false) => Sized(true, size, strong);

	private readonly Dictionary<(string[] Families, int Pixels), TextFont> _families = [];

	public ITextFont GetFamily(string[] families, float size) {
		int pixels = Math.Max(1, (int)MathF.Round(size * Scale));

		if (_families.TryGetValue((families, pixels), out TextFont? font)) return font;

		font = new TextFont(_factory, _bundled.Collection, families, pixels);
		_families[(families, pixels)] = font;
		return font;
	}

	private TextFont Sized(bool mono, float size, bool strong) {
		int pixels = Math.Max(1, (int)MathF.Round(size * Scale));

		if (_sized.TryGetValue((mono, pixels, strong), out TextFont? font)) return font;

		font = new TextFont(_factory, _bundled.Collection, mono ? _codeFamily : _uiFamily, pixels, strong ? (mono ? TextWeight.Bold : TextWeight.SemiBold) : TextWeight.Regular);
		_sized[(mono, pixels, strong)] = font;
		return font;
	}

	private void ClearSized() {
		foreach (TextFont font in _sized.Values) font.Dispose();

		foreach (TextFont font in _italic.Values) font.Dispose();

		_sized.Clear();
		_italic.Clear();
	}

	public float CodeZoom { get; private set; } = 1f;

	public bool SetCodeZoom(float zoom) {
		if (MathF.Abs(zoom - CodeZoom) < 0.001f) return false;

		CodeZoom = zoom;

		TextFont oldCode = Code;
		TextFont oldCodeStrong = CodeStrong;
		TextFont oldCodeSmall = CodeSmall;
		TextFont oldCodeLens = CodeLens;

		BuildCode(Scale);

		oldCode.Dispose();
		oldCodeStrong.Dispose();
		oldCodeSmall.Dispose();
		oldCodeLens.Dispose();
		return true;
	}

	public bool Rescale(float scale) {
		if (MathF.Abs(scale - Scale) < 0.001f) return false;

		Rebuild(scale);
		return true;
	}

	public bool Configure(string[] codeFamily, float codeSize, float lineHeight, string[] uiFamily, float uiSize) {
		if (codeFamily.AsSpan().SequenceEqual(_codeFamily) && uiFamily.AsSpan().SequenceEqual(_uiFamily) && codeSize == _codeSize && lineHeight == _lineHeight && uiSize == _uiSize) return false;

		_codeFamily = codeFamily;
		_uiFamily = uiFamily;
		_codeSize = codeSize;
		_lineHeight = lineHeight;
		_uiSize = uiSize;

		Rebuild(Scale);
		return true;
	}

	private void Rebuild(float scale) {
		TextFont oldCode = Code;
		TextFont oldCodeStrong = CodeStrong;
		TextFont oldCodeSmall = CodeSmall;
		TextFont oldCodeLens = CodeLens;
		TextFont oldUi = Ui;
		TextFont oldUiStrong = UiStrong;
		TextFont oldUiSmall = UiSmall;
		TextFont oldUiDescription = UiDescription;

		Build(scale);
		Scale = scale;
		ClearSized();

		oldCode.Dispose();
		oldCodeStrong.Dispose();
		oldCodeSmall.Dispose();
		oldCodeLens.Dispose();
		oldUi.Dispose();
		oldUiStrong.Dispose();
		oldUiSmall.Dispose();
		oldUiDescription.Dispose();
	}

	private void BuildCode(float scale) {
		float codePixels = MathF.Round(_codeSize * CodeZoom * scale);
		DWrite.IDWriteFontCollection? bundled = _bundled.Collection;
		float lineHeight = _lineHeight > 0f ? _lineHeight * CodeZoom * scale : codePixels * Theming.Metrics.EditorLineHeightRatio;

		Code = new TextFont(_factory, bundled, _codeFamily, codePixels, TextWeight.Regular, lineHeight);
		CodeStrong = new TextFont(_factory, bundled, _codeFamily, codePixels, TextWeight.Bold, lineHeight);
		CodeSmall = new TextFont(_factory, bundled, _codeFamily, MathF.Round(codePixels * 0.85f), TextWeight.Regular, lineHeight);

		float lensPixels = MathF.Floor(codePixels * 0.9f);
		CodeLens = new TextFont(_factory, bundled, _codeFamily, lensPixels, TextWeight.Regular, MathF.Floor(lensPixels * MathF.Max(1.3f, lineHeight / codePixels)));
	}

	private void Build(float scale) {
		DWrite.IDWriteFontCollection? bundled = _bundled.Collection;

		BuildCode(scale);

		Ui = new TextFont(_factory, bundled, _uiFamily, MathF.Round(_uiSize * scale));
		UiStrong = new TextFont(_factory, bundled, _uiFamily, MathF.Round(_labelSize * scale), TextWeight.SemiBold);
		UiSmall = new TextFont(_factory, bundled, _uiFamily, MathF.Round(9f * scale), TextWeight.SemiBold);
		UiDescription = new TextFont(_factory, bundled, _uiFamily, MathF.Round(_uiSize * scale * 0.9f));
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		ClearSized();
		Code?.Dispose();
		CodeStrong?.Dispose();
		CodeSmall?.Dispose();
		CodeLens?.Dispose();
		Ui?.Dispose();
		UiStrong?.Dispose();
		UiSmall?.Dispose();
		UiDescription?.Dispose();
		Icons?.Dispose();
		_bundled.Dispose();
	}
}
