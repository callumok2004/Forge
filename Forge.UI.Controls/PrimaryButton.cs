using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class PrimaryButton : Button
{
	public PrimaryButton() {
		Alignment = TextAlignment.Center;
		Foreground = Theme.ButtonForeground;
		Padding = new Thickness(Metrics.Space8, 0f, Metrics.Space8, 0f);
	}

	public override void PaintBackground(IPainter painter) {
		Color background = !Enabled ? Theme.DisabledForeground : IsCursorOver ? Theme.ButtonHoverBackground : Theme.ButtonBackground;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Radius * Scale, background);
	}

	public override void Paint(IPainter painter) => PaintContents(painter, Theme.ButtonForeground);
}
