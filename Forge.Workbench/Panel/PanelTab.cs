using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class PanelTab : Button
{
	private const float ItemPadding = 10f;
	private const float IndicatorInset = 2f;
	private const float IndicatorHeight = 24f;
	private const float IconBox = 16f;
	private const float TabIconSize = 14f;
	private const float TabIconGap = 5f;
	private const float LabelSize = 13f;
	private const float InactiveIconOpacity = 0.8f;

	private readonly Codicon _icon;
	private readonly string _title;

	public PanelTab(string title, Codicon icon) {
		Name = title;
		Dock = DockType.Left;
		Foreground = Theme.TabInactiveForeground;
		HoverBackground = Theme.ListHoverBackground;
		_icon = icon;
		_title = title;
	}

	private DragGesture _gesture;

	public string? DragContainerId { get; init; }

	public Action<float, float>? DragReleased { get; init; }

	public Action<float, float>? DragMoved { get; init; }

	public float PreferredWidth {
		get {
			if (Ui is null) return 0f;

			float scale = Scale;
			return ((ItemPadding * 2f) + IconBox + TabIconGap) * scale + Ui.GetFonts().GetUi(LabelSize, false).Measure(_title);
		}
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button == MouseButton.Left && (DragContainerId is not null || DragReleased is not null)) {
			_gesture.Press(x, y);
			Ui?.SetMouseCapture(this);
		}

		return base.OnMousePressed(button, x, y);
	}

	public override void OnCursorMoved(float x, float y) {
		if (DragReleased is not null) {
			if (_gesture.Move(x, y, Scale) || _gesture.Dragging) DragMoved?.Invoke(ScreenX + x, ScreenY + y);
		}
		else if (DragContainerId is { } id && _gesture.Move(x, y, Scale)) ViewDrag.Start(id, this, x, y);
		else if (_gesture.Dragging) ViewDrag.Move(this, x, y);

		base.OnCursorMoved(x, y);
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || (DragContainerId is null && DragReleased is null)) return base.OnMouseReleased(button, x, y);

		bool dragged = _gesture.Release();
		Ui?.SetMouseCapture(null);

		if (!dragged) return base.OnMouseReleased(button, x, y);

		if (DragReleased is { } released) {
			released(ScreenX + x, ScreenY + y);
			return true;
		}

		ViewDrag.End(this, x, y);
		return true;
	}

	public override void OnMouseCaptureLost() {
		if (_gesture.Release() && DragReleased is null) ViewDrag.End(this, 0f, 0f, cancelled: true);
	}

	public bool Selected {
		get;
		set {
			if (field == value) return;

			field = value;
			Foreground = value ? Theme.TabActiveForeground : Theme.TabInactiveForeground;
			Repaint();
		}
	}

	public override void PaintBackground(IPainter painter) {
		if (!Selected && !IsCursorOver) return;

		float scale = Scale;
		float inset = IndicatorInset * scale;
		float height = IndicatorHeight * scale;
		painter.FillRoundedRectangle(ScreenX + inset, ScreenY + ((Tall - height) / 2f), Wide - (inset * 2f), height, Metrics.RadiusSmall * scale, Selected ? Theme.ActivityBarActiveBackground : HoverBackground);
	}

	public override void Paint(IPainter painter) {
		if (Ui is null) return;

		float scale = Scale;
		float left = ScreenX + (ItemPadding * scale);
		float centreY = ScreenY + (Tall / 2f);
		Color icon = Selected ? Theme.ChromeForeground : Theme.ChromeForeground.WithAlpha(InactiveIconOpacity);
		ITextFont font = Ui.GetFonts().GetUi(LabelSize, false);

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);
		painter.DrawIcon(_icon, left + (IconBox * scale / 2f), centreY, TabIconSize * scale, icon);
		painter.DrawText(font, _title, left + ((IconBox + TabIconGap) * scale), centreY - (font.GetLineHeight() / 2f) + font.GetBaseline(), Foreground);
		painter.PopClip();
	}
}
