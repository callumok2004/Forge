using Forge.Common.UI;
using Forge.UI.Controls;

namespace Forge.Workbench;

public interface IViewDragTarget
{
	void DragStarted(string containerId, float x, float y);

	void DragMoved(float x, float y);

	void DragEnded(float x, float y, bool cancelled);
}

public static class ViewDrag
{
	public const float Threshold = 5f;

	public static IViewDragTarget? Target { get; set; }

	public static bool Active { get; private set; }

	public static void Start(string containerId, Panel source, float x, float y) {
		if (Target is null) return;

		Active = true;
		(float absoluteX, float absoluteY) = source.GetAbsPos();
		Target.DragStarted(containerId, absoluteX + x, absoluteY + y);
	}

	public static void Move(Panel source, float x, float y) {
		if (!Active) return;

		(float absoluteX, float absoluteY) = source.GetAbsPos();
		Target?.DragMoved(absoluteX + x, absoluteY + y);
	}

	public static void End(Panel source, float x, float y, bool cancelled = false) {
		if (!Active) return;

		Active = false;
		(float absoluteX, float absoluteY) = source.GetAbsPos();
		Target?.DragEnded(absoluteX + x, absoluteY + y, cancelled);
	}
}

public struct DragGesture
{
	private float _startX;
	private float _startY;
	private bool _pressed;

	public bool Dragging { get; private set; }

	public void Press(float x, float y) {
		_pressed = true;
		Dragging = false;
		_startX = x;
		_startY = y;
	}

	public bool Move(float x, float y, float scale) {
		if (!_pressed || Dragging) return false;

		if (MathF.Abs(x - _startX) < ViewDrag.Threshold * scale && MathF.Abs(y - _startY) < ViewDrag.Threshold * scale) return false;

		Dragging = true;
		return true;
	}

	public bool Release() {
		bool dragged = Dragging;
		_pressed = false;
		Dragging = false;
		return dragged;
	}
}

public sealed class ContainerButton : ActionButton
{
	private DragGesture _gesture;

	public ContainerButton(string containerId) {
		ContainerId = containerId;
		Name = containerId;
	}

	public string ContainerId { get; }

	public bool Movable { get; init; } = true;

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button == MouseButton.Left && Movable) {
			_gesture.Press(x, y);
			Ui?.SetMouseCapture(this);
		}

		return base.OnMousePressed(button, x, y);
	}

	public override void OnCursorMoved(float x, float y) {
		if (_gesture.Move(x, y, Scale)) ViewDrag.Start(ContainerId, this, x, y);
		else if (_gesture.Dragging) ViewDrag.Move(this, x, y);

		base.OnCursorMoved(x, y);
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return base.OnMouseReleased(button, x, y);

		bool dragged = _gesture.Release();
		Ui?.SetMouseCapture(null);

		if (!dragged) return base.OnMouseReleased(button, x, y);

		ViewDrag.End(this, x, y);
		Repaint();
		return true;
	}

	public override void OnMouseCaptureLost() {
		if (_gesture.Release()) ViewDrag.End(this, 0f, 0f, cancelled: true);
	}
}
