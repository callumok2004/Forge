using DWrite = Vortice.DirectWrite;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class FontSet : IFontSet, IDisposable
{
	private readonly DWrite.IDWriteFactory _factory;
	private readonly string[] _codeFamily;
	private readonly string[] _uiFamily;
	private readonly float _codeSize;
	private readonly float _uiSize;
	private readonly float _labelSize;
	private readonly float _lineHeight;

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

		Scale = scale;
		Build(scale);
	}

	public TextFont Code { get; private set; } = null!;

	public TextFont CodeStrong { get; private set; } = null!;

	public TextFont CodeSmall { get; private set; } = null!;

	public TextFont Ui { get; private set; } = null!;

	public TextFont UiStrong { get; private set; } = null!;

	public TextFont UiSmall { get; private set; } = null!;

	public IconFont Icons { get; }

	public float Scale { get; private set; }

	public ITextFont GetCode() => Code;

	public ITextFont GetCodeStrong() => CodeStrong;

	public ITextFont GetCodeSmall() => CodeSmall;

	public ITextFont GetUi() => Ui;

	public ITextFont GetUiStrong() => UiStrong;

	public ITextFont GetUiSmall() => UiSmall;

	public float GetScale() => Scale;

	public bool Rescale(float scale) {
		if (MathF.Abs(scale - Scale) < 0.001f) return false;

		TextFont oldCode = Code;
		TextFont oldCodeStrong = CodeStrong;
		TextFont oldCodeSmall = CodeSmall;
		TextFont oldUi = Ui;
		TextFont oldUiStrong = UiStrong;
		TextFont oldUiSmall = UiSmall;

		Build(scale);
		Scale = scale;

		oldCode.Dispose();
		oldCodeStrong.Dispose();
		oldCodeSmall.Dispose();
		oldUi.Dispose();
		oldUiStrong.Dispose();
		oldUiSmall.Dispose();

		return true;
	}

	private void Build(float scale) {
		float codePixels = MathF.Round(_codeSize * scale);

		Code = new TextFont(_factory, _codeFamily, codePixels, TextWeight.Regular,
			_lineHeight > 0f ? _lineHeight * scale : codePixels * Theming.Metrics.EditorLineHeightRatio);

		CodeStrong = new TextFont(_factory, _codeFamily, codePixels, TextWeight.Bold,
			_lineHeight > 0f ? _lineHeight * scale : codePixels * Theming.Metrics.EditorLineHeightRatio);

		CodeSmall = new TextFont(_factory, _codeFamily, MathF.Round(codePixels * 0.85f),
			TextWeight.Regular,
			_lineHeight > 0f ? _lineHeight * scale : codePixels * Theming.Metrics.EditorLineHeightRatio);

		Ui = new TextFont(_factory, _uiFamily, MathF.Round(_uiSize * scale));
		UiStrong = new TextFont(_factory, _uiFamily, MathF.Round(_labelSize * scale), TextWeight.SemiBold);
		UiSmall = new TextFont(_factory, _uiFamily, MathF.Round(9f * scale), TextWeight.SemiBold);
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		Code?.Dispose();
		CodeStrong?.Dispose();
		CodeSmall?.Dispose();
		Ui?.Dispose();
		UiStrong?.Dispose();
		UiSmall?.Dispose();
		Icons?.Dispose();
	}
}
