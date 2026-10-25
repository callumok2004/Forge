using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class Separator : Panel
{
	public Separator() => MouseInputEnabled = false;

	public Color Color { get; set; } = Theme.TitleBarForeground.WithAlpha(0.18f);

	public float Length { get; set; } = 16f;

	public override void Paint(IPainter painter) {
		float half = Length * Scale / 2f;
		float centreY = ScreenY + (Tall / 2f);
		painter.DrawVerticalLine(MathF.Round(ScreenX + (Wide / 2f)), centreY - half, centreY + half, Color);
	}
}
