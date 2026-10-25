using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class CommandCentreButton : Button
{
	private const float HorizontalPadding = 12f;

	public CommandCentreButton() {
		Name = "CommandCentre";
		Command = "workbench.action.quickOpen";
		Alignment = TextAlignment.Center;
		Foreground = Theme.CommandCentreForeground;
		Radius = Metrics.RadiusMedium;
		Padding = new Thickness(HorizontalPadding, 0f, HorizontalPadding, 0f);
		IconColor = _ => Theme.CommandCentreForeground.WithAlpha(0.8f);
	}

	public float PreferredWidth => Ui is null ? 0f : MeasureContents(FontFor(Ui.GetFonts()), Scale) + (HorizontalPadding * 2f * Scale);

	public override void PaintBackground(IPainter painter) {
		float radius = Radius * Scale;
		bool hovered = IsCursorOver;

		if (hovered) painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.CommandCentreHoverBackground);

		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * Scale, hovered ? Theme.CommandCentreActiveBorder : Theme.CommandCentreBorder);
	}
}
