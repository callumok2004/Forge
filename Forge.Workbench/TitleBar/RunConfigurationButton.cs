using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class RunConfigurationButton : Button
{
	private const float DotSize = 7f;
	private const float Gap = 6f;
	private const float LeadingPadding = 8f;

	public RunConfigurationButton() {
		Name = "RunConfiguration";
		Command = "forge.debug.pickConfiguration";
		Foreground = Theme.TitleBarForeground;
		Radius = Metrics.RadiusMedium;
		IconColor = _ => Theme.TitleBarForeground.WithAlpha(0.55f);
		SizeToContents = true;
		ApplyPadding();
	}

	public bool Debugging {
		get;
		set {
			if (field == value) return;

			field = value;
			ApplyPadding();
		}
	}

	public bool Paused {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	private Color Tint => Paused ? Theme.DebugIconPause : Theme.DebugIconStart;

	private void ApplyPadding() {
		Padding = new Thickness(LeadingPadding + (Debugging ? DotSize + Gap : 0f), 0f, Gap, 0f);

		if (Ui is not null) FitContents();

		Repaint();
	}

	public override void PaintBackground(IPainter painter) {
		if (Debugging) painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Radius * Scale, Tint.WithAlpha(0.16f));
		else base.PaintBackground(painter);
	}

	public override void Paint(IPainter painter) {
		if (Debugging) {
			float scale = Scale;
			float dot = DotSize * scale;
			float centreX = ScreenX + (LeadingPadding * scale) + (dot / 2f);
			float centreY = ScreenY + (Tall / 2f);

			painter.FillCircle(centreX, centreY, dot, Tint.WithAlpha(0.25f));
			painter.FillCircle(centreX, centreY, dot / 2f, Tint);
		}

		base.Paint(painter);
	}
}
