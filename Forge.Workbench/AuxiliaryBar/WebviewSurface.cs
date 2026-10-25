using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Common.Webview;
using Forge.Core;

namespace Forge.Workbench;

public sealed class WebviewSurface : Panel
{
	private readonly AuxiliaryBar _bar;

	public WebviewSurface(AuxiliaryBar bar) {
		_bar = bar;
		Name = "WebviewSurface";
		Dock = DockType.Fill;
	}

	private void Send(WebviewMouse kind, float x, float y, int delta = 0) {
		if (_bar.ShownViewId is { } id) _bar.Webviews.SendMouse(id, kind, (int)x, (int)y, delta);
	}

	public override void OnCursorMoved(float x, float y) => Send(WebviewMouse.Move, x, y);

	public override void OnCursorExited() => Send(WebviewMouse.Leave, 0f, 0f);

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return true;

		Ui?.SetMouseCapture(this);
		Send(WebviewMouse.LeftDown, x, y);
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		Send(WebviewMouse.LeftUp, x, y);
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		Send(WebviewMouse.Wheel, x, y, delta);
		return true;
	}

	public override void Paint(IPainter painter) {
		if (_bar.ShownViewId is null) return;

		PixelRect content = _bar.ContentBounds;
		painter.ClearRectangle(content.Left, content.Top, content.Width, content.Height);
	}
}
