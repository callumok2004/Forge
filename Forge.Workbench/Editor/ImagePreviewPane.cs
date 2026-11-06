using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Workbench;

public sealed class ImagePreviewPane : EditorPane
{
	private const float PixelationThreshold = 3f;
	private const float PinchFactor = 0.075f;
	private const float MaxScale = 20f;
	private const float MinScale = 0.1f;
	private const float FitInset = 20f;
	private const float CheckerSize = 8f;
	private const float WheelStep = 40f;

	private static readonly float[] ZoomLevels = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f, 1.5f, 2f, 3f, 5f, 7f, 10f, 15f, 20f];

	private static readonly Color CheckerColor = Color.Parse("#141414");

	private readonly string _path;
	private readonly Func<string, IImage?> _load;
	private IImage? _image;
	private bool _loaded;
	private float? _scale;
	private float _scrollX;
	private float _scrollY;

	public ImagePreviewPane(string path, Func<string, IImage?> load) {
		_path = path;
		_load = load;
		Id = path;
		Name = "ImagePreview";
		Dock = DockType.Fill;
	}

	public override string Title => Path.GetFileName(_path);

	public override Codicon Icon => Codicon.File;

	public string FilePath => _path;

	public Action<float?>? ZoomChanged { get; set; }

	public float? Zoom => _scale;

	private IImage? Image {
		get {
			if (_loaded) return _image;

			_loaded = true;
			_image = _load(_path);

			if (_image is IAnimatedImage animated) Schedule(animated.CurrentDelay);

			return _image;
		}
	}

	private int _scheduledDelay;

	private void Schedule(int delay) {
		if (delay == _scheduledDelay) return;

		_scheduledDelay = delay;
		RemoveTickSignal();
		AddTickSignal(delay);
	}

	public override void OnTick() {
		if (_image is not IAnimatedImage animated || !IsVisibleOnScreen()) return;

		animated.Advance();
		Schedule(animated.CurrentDelay);
		Repaint();
	}

	private bool IsVisibleOnScreen() {
		for (Panel? current = this; current is not null; current = current.Parent) {
			if (!current.Visible) return false;
		}

		return true;
	}

	public override void OnClosed() {
		RemoveTickSignal();
		(_image as IDisposable)?.Dispose();
		_image = null;
	}

	private (float Width, float Height) Natural => Image is { } image ? (image.GetSourceWidth(), image.GetSourceHeight()) : (0f, 0f);

	private float FitScale {
		get {
			(float width, float height) = Natural;

			if (width <= 0f || height <= 0f) return 1f;

			float scale = Scale;
			float available = Math.Max(1f, Wide - (FitInset * scale));
			float availableHeight = Math.Max(1f, Tall - (FitInset * scale));

			return Math.Min(scale, Math.Min(available / width, availableHeight / height));
		}
	}

	private float DisplayScale => _scale is { } zoom ? zoom * Scale : FitScale;

	private (float Width, float Height) Displayed {
		get {
			(float width, float height) = Natural;
			float scale = DisplayScale;
			return (width * scale, height * scale);
		}
	}

	private void SetScale(float? value) {
		if (value is { } zoom) {
			(float width, float height) = Displayed;
			float centreX = width > Wide ? (_scrollX + (Wide / 2f)) / width : 0.5f;
			float centreY = height > Tall ? (_scrollY + (Tall / 2f)) / height : 0.5f;

			_scale = Math.Clamp(zoom, MinScale, MaxScale);

			(width, height) = Displayed;
			_scrollX = (width * centreX) - (Wide / 2f);
			_scrollY = (height * centreY) - (Tall / 2f);
		}
		else {
			_scale = null;
		}

		ClampScroll();
		ZoomChanged?.Invoke(_scale);
		Repaint();
	}

	private float CurrentZoom => _scale ?? (FitScale / Math.Max(Scale, 0.01f));

	public void ZoomIn() {
		float current = CurrentZoom;

		foreach (float level in ZoomLevels) {
			if (level <= current) continue;

			SetScale(level);
			return;
		}

		SetScale(MaxScale);
	}

	public void ZoomOut() {
		float current = CurrentZoom;

		for (int i = ZoomLevels.Length - 1; i >= 0; i--) {
			if (ZoomLevels[i] >= current) continue;

			SetScale(ZoomLevels[i]);
			return;
		}

		SetScale(MinScale);
	}

	public void ResetZoom() => SetScale(null);

	private void ClampScroll() {
		(float width, float height) = Displayed;
		_scrollX = Math.Clamp(_scrollX, 0f, Math.Max(0f, width - Wide));
		_scrollY = Math.Clamp(_scrollY, 0f, Math.Max(0f, height - Tall));
	}

	public override void OnSizeChanged(float wide, float tall) => ClampScroll();

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || Image is null) return false;

		RequestFocus();

		if ((Ui?.GetModifiers() ?? KeyModifiers.None).HasFlag(KeyModifiers.Control)) ZoomOut();
		else ZoomIn();

		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		if (Image is null) return false;

		KeyModifiers modifiers = Ui?.GetModifiers() ?? KeyModifiers.None;

		if (modifiers.HasFlag(KeyModifiers.Control)) {
			float direction = delta < 0 ? 1f : -1f;
			SetScale(CurrentZoom * (1f - (direction * PinchFactor)));
			return true;
		}

		float step = -delta / 120f * WheelStep * Scale;

		if (modifiers.HasFlag(KeyModifiers.Shift)) _scrollX += step;
		else _scrollY += step;

		ClampScroll();
		Repaint();
		return true;
	}

	public override void Paint(IPainter painter) {
		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.EditorBackground);

		if (Image is not { } image) {
			if (!_loaded) return;

			ITextFont font = painter.GetUi();
			const string message = "An error occurred while loading the image.";
			painter.DrawText(font, message, ScreenX + ((Wide - font.Measure(message)) / 2f), ScreenY + (Tall / 2f), Theme.EditorForeground);
			return;
		}

		(float width, float height) = Displayed;
		float left = MathF.Round(ScreenX + (width < Wide ? (Wide - width) / 2f : -_scrollX));
		float top = MathF.Round(ScreenY + (height < Tall ? (Tall - height) / 2f : -_scrollY));

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);
		PaintChecker(painter, left, top, width, height);
		painter.DrawImage(image, left, top, width, height, _scale >= PixelationThreshold);
		painter.PopClip();
	}

	private void PaintChecker(IPainter painter, float left, float top, float width, float height) {
		float cell = CheckerSize * Scale;
		float visibleLeft = Math.Max(left, ScreenX);
		float visibleTop = Math.Max(top, ScreenY);
		float visibleRight = Math.Min(left + width, ScreenX + Wide);
		float visibleBottom = Math.Min(top + height, ScreenY + Tall);

		int firstColumn = (int)((visibleLeft - left) / cell);
		int firstRow = (int)((visibleTop - top) / cell);

		for (int row = firstRow; top + (row * cell) < visibleBottom; row++) {
			float y = top + (row * cell);

			for (int column = firstColumn + ((firstColumn + row + 1) & 1); left + (column * cell) < visibleRight; column += 2) {
				float x = left + (column * cell);
				painter.FillRectangle(x, y, Math.Min(cell, left + width - x), Math.Min(cell, top + height - y), CheckerColor);
			}
		}
	}
}
