using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class PanelTab : Button
{
	private const float HighlightHeight = 22f;
	private const float UnderlineHeight = 2f;

	public PanelTab(string title) {
		Name = title;
		Dock = DockType.Left;
		Padding = new Thickness(Metrics.Space8, 0f, Metrics.Space8, 0f);
		Foreground = Theme.TabInactiveForeground;
		HoverBackground = Theme.ListHoverBackground;
		SizeToContents = true;
		SetText(title);
	}

	private DragGesture _gesture;

	public string? DragContainerId { get; init; }

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button == MouseButton.Left && DragContainerId is not null) {
			_gesture.Press(x, y);
			Ui?.SetMouseCapture(this);
		}

		return base.OnMousePressed(button, x, y);
	}

	public override void OnCursorMoved(float x, float y) {
		if (DragContainerId is { } id && _gesture.Move(x, y, Scale)) ViewDrag.Start(id, this, x, y);
		else if (_gesture.Dragging) ViewDrag.Move(this, x, y);

		base.OnCursorMoved(x, y);
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || DragContainerId is null) return base.OnMouseReleased(button, x, y);

		bool dragged = _gesture.Release();
		Ui?.SetMouseCapture(null);

		if (!dragged) return base.OnMouseReleased(button, x, y);

		ViewDrag.End(this, x, y);
		return true;
	}

	public override void OnMouseCaptureLost() {
		if (_gesture.Release()) ViewDrag.End(this, 0f, 0f, cancelled: true);
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
		if (Selected || !IsCursorOver) return;

		float scale = Scale;
		float height = HighlightHeight * scale;
		painter.FillRoundedRectangle(ScreenX, ScreenY + ((Tall - height) / 2f), Wide, height, Metrics.RadiusSmall * scale, HoverBackground);
	}

	public override void Paint(IPainter painter) {
		PaintContents(painter, Foreground);

		if (!Selected) return;

		float scale = Scale;
		float inset = Padding.Left * scale;
		painter.FillRectangle(ScreenX + inset, ScreenY + Tall - (UnderlineHeight * scale), Wide - (inset * 2f), UnderlineHeight * scale, Theme.TabActiveBorderTop);
	}
}
