using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Workbench;

public sealed class PanelPaneHeader : Panel
{
	public const float Height = 28f;

	private const float TitleSize = 12f;
	private const float Inset = 4f;

	private readonly string _title;
	private DragGesture _gesture;

	public PanelPaneHeader(string title) {
		_title = title;
		Name = title + ".header";
		Dock = DockType.None;
	}

	public Action? Clicked { get; set; }

	public Action<float, float>? DragReleased { get; set; }

	public Action<float, float>? DragMoved { get; set; }

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() => Repaint();

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		_gesture.Press(x, y);
		Ui?.SetMouseCapture(this);
		return true;
	}

	public override void OnCursorMoved(float x, float y) {
		if (_gesture.Move(x, y, Scale) || _gesture.Dragging) DragMoved?.Invoke(ScreenX + x, ScreenY + y);
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		bool dragged = _gesture.Release();
		Ui?.SetMouseCapture(null);

		if (dragged) DragReleased?.Invoke(ScreenX + x, ScreenY + y);
		else Clicked?.Invoke();

		return true;
	}

	public override void OnMouseCaptureLost() => _gesture.Release();

	public override void Paint(IPainter painter) {
		if (Ui is null) return;

		float scale = Scale;
		float inset = Inset * scale;

		if (IsCursorOver) painter.FillRoundedRectangle(ScreenX + inset, ScreenY, Wide - (inset * 2f), Tall, Metrics.RadiusSmall * scale, Theme.ListHoverBackground);

		ITextFont font = Ui.GetFonts().GetUi(TitleSize, true);
		float centreY = ScreenY + (Tall / 2f);
		float left = ScreenX + inset;

		painter.DrawIcon(Codicon.ChevronDown, left + (Metrics.IconSize * scale / 2f) + (2f * scale), centreY, Metrics.IconSize * scale, Theme.SectionHeaderForeground);
		painter.DrawText(font, _title, left + ((Metrics.IconSize + 4f) * scale), centreY - (font.GetLineHeight() / 2f) + font.GetBaseline(), Theme.SectionHeaderForeground);
	}
}
