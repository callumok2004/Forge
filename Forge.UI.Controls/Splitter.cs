using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class Splitter : Panel
{
	private const float SashSize = 4f;

	private bool _dragging;

	public Splitter(bool vertical) {
		Vertical = vertical;
		Cursor = vertical ? CursorCode.SizeWestEast : CursorCode.SizeNorthSouth;
	}

	public bool Vertical { get; }

	public Action<float, float>? Dragged { get; set; }

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() => Repaint();

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return true;

		_dragging = true;
		Ui?.SetMouseCapture(this);
		Repaint();
		return true;
	}

	public override void OnCursorMoved(float x, float y) {
		if (!_dragging) return;

		(float absX, float absY) = GetAbsPos();
		Dragged?.Invoke(absX + x, absY + y);
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_dragging) return true;

		_dragging = false;

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		Repaint();
		return true;
	}

	public override void OnMouseCaptureLost() => _dragging = false;

	public override void Paint(IPainter painter) {
		if (!_dragging && !IsCursorOver) return;

		float sash = SashSize * Scale;

		if (Vertical) painter.FillRectangle(ScreenX + ((Wide - sash) / 2f), ScreenY, sash, Tall, Theme.FocusBorder);
		else painter.FillRectangle(ScreenX, ScreenY + ((Tall - sash) / 2f), Wide, sash, Theme.FocusBorder);
	}
}
