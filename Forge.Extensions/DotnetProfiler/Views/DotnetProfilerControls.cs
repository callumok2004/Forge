using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class TabBar : Panel
{
	private const float FontSize = 12f;
	private const float PaddingX = 14f;
	private const float Gap = 2f;

	private readonly string[] _labels;
	private int _hover = -1;

	public TabBar(string[] labels) {
		_labels = labels;
		Name = "Tabs";
	}

	public int Selected {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
			Changed?.Invoke(value);
		}
	}

	public Action<int>? Changed { get; set; }

	public static float PreferredHeight(float scale) => 31f * scale;

	private int TabAt(float x) {
		if (Ui is null) return -1;

		ITextFont font = Ui.GetFonts().GetUi(FontSize);
		float scale = Scale;
		float cursor = 8f * scale;

		for (int i = 0; i < _labels.Length; i++) {
			float width = font.Measure(_labels[i]) + (PaddingX * 2f * scale);

			if (x >= cursor && x < cursor + width) return i;

			cursor += width + (Gap * scale);
		}

		return -1;
	}

	public override void OnCursorMoved(float x, float y) {
		int hover = TabAt(x);

		if (hover == _hover) return;

		_hover = hover;
		Cursor = hover >= 0 ? CursorCode.Hand : CursorCode.Arrow;
		Repaint();
	}

	public override void OnCursorExited() {
		_hover = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || TabAt(x) is not (var index and >= 0)) return false;

		Selected = index;
		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi(FontSize);
		float cursor = ScreenX + (8f * scale);
		float baseline = ScreenY + ((Tall - (2f * scale) - font.GetLineHeight()) / 2f) + font.GetBaseline();

		for (int i = 0; i < _labels.Length; i++) {
			float width = font.Measure(_labels[i]) + (PaddingX * 2f * scale);
			bool selected = i == Selected;

			painter.DrawText(font, _labels[i], cursor + (PaddingX * scale), baseline, Theme.EditorForeground.WithAlpha(selected || i == _hover ? 1f : 0.7f));

			if (selected) painter.FillRectangle(cursor, ScreenY + Tall - (3f * scale), width, 2f * scale, Theme.FocusBorder);

			cursor += width + (Gap * scale);
		}

		painter.DrawHorizontalLine(ScreenY + Tall - 1f, ScreenX, ScreenX + Wide, Theme.PanelBorder);
	}
}

public sealed class Segmented : Panel
{
	private const float AnimationSeconds = 0.15f;

	private readonly string[] _options;
	private int _hover = -1;
	private float _from;
	private float _progress = 1f;

	public Segmented(params string[] options) {
		_options = options;
		Name = string.Join('|', options);
	}

	public float FontSize { get; init; } = 11f;

	public int Selected {
		get;
		set {
			if (field == value) return;

			_from = Ui is null || !IsShowing ? value : CurrentPosition();
			_progress = Ui is null || !IsShowing ? 1f : 0f;
			field = value;

			if (_progress < 1f) SetThinking(true);

			Repaint();
		}
	}

	public Action<int>? Changed { get; set; }

	private float CurrentPosition() {
		float eased = 1f - MathF.Pow(1f - _progress, 3f);
		return _from + ((Selected - _from) * eased);
	}

	public float PreferredWidth(ITextFont font, float scale) {
		float width = 0f;

		foreach (string option in _options) width += font.Measure(option) + (16f * scale);

		return width;
	}

	private float OptionLeft(ITextFont font, float scale, int index) {
		float cursor = 0f;

		for (int i = 0; i < index && i < _options.Length; i++) cursor += font.Measure(_options[i]) + (16f * scale);

		return cursor;
	}

	private float OptionWidth(ITextFont font, float scale, int index) => (uint)index < (uint)_options.Length ? font.Measure(_options[index]) + (16f * scale) : 0f;

	private int OptionAt(float x) {
		if (Ui is null) return -1;

		ITextFont font = Ui.GetFonts().GetUi(FontSize);
		float scale = Scale;

		for (int i = 0; i < _options.Length; i++) {
			float left = OptionLeft(font, scale, i);

			if (x >= left && x < left + OptionWidth(font, scale, i)) return i;
		}

		return -1;
	}

	public override void OnThink(float deltaSeconds) {
		_progress = MathF.Min(1f, _progress + (deltaSeconds / AnimationSeconds));

		if (_progress >= 1f) SetThinking(false);

		Repaint();
	}

	public override void OnCursorMoved(float x, float y) {
		int hover = OptionAt(x);

		if (hover == _hover) return;

		_hover = hover;
		Cursor = hover >= 0 && hover != Selected ? CursorCode.Hand : CursorCode.Arrow;
		Repaint();
	}

	public override void OnCursorExited() {
		_hover = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		if (OptionAt(x) is var index and >= 0 && index != Selected) {
			Selected = index;
			Changed?.Invoke(index);
		}

		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi(FontSize);
		float radius = 3f * scale;
		float baseline = ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.InputBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.InputBorder);

		float position = CurrentPosition();
		int lower = Math.Clamp((int)MathF.Floor(position), 0, _options.Length - 1);
		int upper = Math.Clamp(lower + 1, 0, _options.Length - 1);
		float t = position - lower;
		float indicatorX = OptionLeft(font, scale, lower) + ((OptionLeft(font, scale, upper) - OptionLeft(font, scale, lower)) * t);
		float indicatorWidth = OptionWidth(font, scale, lower) + ((OptionWidth(font, scale, upper) - OptionWidth(font, scale, lower)) * t);

		if (_hover >= 0 && _hover != Selected) painter.FillRoundedRectangle(ScreenX + OptionLeft(font, scale, _hover) + (2f * scale), ScreenY + (2f * scale), OptionWidth(font, scale, _hover) - (4f * scale), Tall - (4f * scale), radius, Theme.ListHoverBackground);

		painter.FillRoundedRectangle(ScreenX + indicatorX + (2f * scale), ScreenY + (2f * scale), indicatorWidth - (4f * scale), Tall - (4f * scale), radius, Theme.ListActiveSelectionBackground);

		for (int i = 0; i < _options.Length; i++) painter.DrawText(font, _options[i], ScreenX + OptionLeft(font, scale, i) + (8f * scale), baseline, i == Selected ? Theme.EditorForeground : Theme.DescriptionForeground);
	}
}

public sealed class FlowRow : Panel
{
	private const float Gap = 8f;

	public FlowRow() {
		Name = "Toolbar";
		MouseInputEnabled = false;
	}

	public static float PreferredHeight(float scale) => 36f * scale;

	public Func<Panel, float>? WidthOf { get; set; }

	public HashSet<Panel> RightAligned { get; } = [];

	public bool Border { get; init; } = true;

	public Panel? Fill { get; set; }

	public override void PaintBorder(IPainter painter) {
		if (Border) painter.DrawHorizontalLine(ScreenY + Tall - 1f, ScreenX, ScreenX + Wide, Theme.PanelBorder);
	}

	public override void PerformLayout() {
		float scale = Scale;
		float height = 22f * scale;
		float top = (Tall - height) / 2f;
		float left = 10f * scale;
		float right = Wide - (10f * scale);

		foreach (Panel child in Children) {
			if (!child.Visible || ReferenceEquals(child, Fill)) continue;

			float width = WidthOf?.Invoke(child) ?? child.Wide;

			if (RightAligned.Contains(child)) {
				right -= width;
				child.SetBounds(right, top, width, height);
				right -= Gap * scale;
				continue;
			}

			child.SetBounds(left, top, width, height);
			left += width + (Gap * scale);
		}

		Fill?.SetBounds(left, top, MathF.Max(0f, right - left), height);
	}
}

public sealed class SplitPane : Panel
{
	private const float Handle = 6f;
	private const float MinimumDetail = 240f;

	private readonly Panel _main;
	private readonly Panel _detail;
	private bool _dragging;

	public SplitPane(Panel main, Panel detail) {
		_main = main;
		_detail = detail;
		Name = "Split";
		AddChild(main);
		AddChild(detail);
	}

	public float DetailWidth { get; set; } = float.NaN;

	private bool _hoverHandle;

	public Action<float>? Resized { get; set; }

	private float DetailPixels() {
		float preferred = float.IsNaN(DetailWidth) ? Math.Clamp(Wide * 0.34f, 300f * Scale, 520f * Scale) : DetailWidth * Scale;

		return Math.Clamp(preferred, MinimumDetail * Scale, MathF.Max(MinimumDetail * Scale, Wide - (260f * Scale)));
	}

	private bool OverHandle(float x) => MathF.Abs(x - (Wide - DetailPixels())) <= Handle * Scale;

	public override Panel? IsWithinTraverse(float screenX, float screenY) {
		if (Visible && IsWithin(screenX, screenY) && OverHandle(screenX - ScreenX)) return this;

		return base.IsWithinTraverse(screenX, screenY);
	}

	public override void OnCursorMoved(float x, float y) {
		if (_dragging) {
			DetailWidth = MathF.Max(MinimumDetail, (Wide - x) / Scale);
			InvalidateLayout();
			Repaint();
			return;
		}

		bool over = OverHandle(x);
		Cursor = over ? CursorCode.SizeWestEast : CursorCode.Arrow;

		if (over == _hoverHandle) return;

		_hoverHandle = over;
		Repaint();
	}

	public override void OnCursorExited() {
		if (!_hoverHandle) return;

		_hoverHandle = false;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || !OverHandle(x)) return false;

		_dragging = true;
		Ui?.SetMouseCapture(this);
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || !OverHandle(x)) return false;

		DetailWidth = float.NaN;
		Resized?.Invoke(DetailWidth);
		InvalidateLayout();
		Repaint();
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_dragging) return false;

		_dragging = false;
		Ui?.SetMouseCapture(null);
		Resized?.Invoke(DetailWidth);
		return true;
	}

	public override void PerformLayout() {
		float detail = DetailPixels();
		_main.SetBounds(0f, 0f, Wide - detail, Tall);
		_detail.SetBounds(Wide - detail, 0f, detail, Tall);
	}

	public override void PostChildPaint(IPainter painter) {
		float x = ScreenX + Wide - DetailPixels();
		bool active = _dragging || _hoverHandle;
		painter.FillRectangle(x - (active ? 0.5f * Scale : 0f), ScreenY, active ? 1f * Scale + 1f : 1f, Tall, active ? Theme.FocusBorder : Theme.PanelBorder.WithAlpha(0.5f));
	}
}
