using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class InputToggle : Panel
{
	public const float Size = 20f;

	public InputToggle(Codicon icon, string title) {
		Icon = icon;
		Title = title;
		MouseInputEnabled = true;
		Cursor = CursorCode.Hand;
	}

	public Codicon Icon { get; }

	public string Title { get; }

	public Action<bool>? Changed { get; set; }

	public bool Checked {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() => Repaint();

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		Checked = !Checked;
		Changed?.Invoke(Checked);
		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusSmall * scale;
		float stroke = Metrics.StrokeThickness * scale;

		if (Checked) {
			painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.InputOptionActiveBackground);
			painter.StrokeRoundedRectangle(ScreenX + (stroke / 2f), ScreenY + (stroke / 2f), Wide - stroke, Tall - stroke, radius, stroke, Theme.InputOptionActiveBorder);
		}
		else if (IsCursorOver) {
			painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.InputOptionHoverBackground);
		}

		painter.DrawIcon(Icon, ScreenX + (Wide / 2f), ScreenY + (Tall / 2f), Metrics.IconSize * scale, Checked ? Theme.InputOptionActiveForeground : Theme.Foreground);
	}
}
