using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public class Button : Label
{
	private bool _pressed;

	public Button() {
		MouseInputEnabled = true;
		Cursor = CursorCode.Arrow;
	}

	public string? Command { get; set; }

	public Action? Action { get; set; }

	public Color HoverBackground { get; set; } = Theme.ToolbarHoverBackground;

	public Color? HoverForeground { get; set; }

	public float Radius { get; set; } = Metrics.RadiusSmall;

	public bool Active {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public bool IsArmed => IsCursorOver;

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() {
		_pressed = false;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || !Enabled) return true;

		_pressed = true;
		Repaint();
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_pressed) return true;

		_pressed = false;
		Repaint();

		if (x < 0f || y < 0f || x >= Wide || y >= Tall) return true;

		DoClick();
		return true;
	}

	public virtual void DoClick() {
		if (Action is { } action) action();
		else if (Command is { } command) PostCommand(command);
	}

	public override void PaintBackground(IPainter painter) {
		if (!(IsCursorOver || Active) || !Enabled) return;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Radius * Scale, _pressed ? Theme.PressedOverlay.Over(HoverBackground) : HoverBackground);
	}

	public override void Paint(IPainter painter) => PaintContents(painter, !Enabled ? Theme.DisabledForeground : IsCursorOver && HoverForeground is { } hover ? hover : Foreground);
}
