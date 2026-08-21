using Forge.Common.Render;
using Forge.Theming;

namespace Forge.UI.Controls;

public static class IconButton
{
	public const float Size = 22f;

	public static void Draw(IPainter painter, Codicon icon, float centreX, float centreY, float scale, bool hovered, bool pressed, Color foreground) {
		float size = Size * scale;

		if (hovered || pressed) {
			painter.FillRoundedRectangle(centreX - (size / 2f), centreY - (size / 2f), size, size, Metrics.RadiusSmall * scale,
				pressed ? Theme.ListActiveSelectionBackground : Theme.ListHoverBackground);
		}

		painter.DrawIcon(icon, centreX, centreY, Metrics.IconSize * scale, foreground);
	}
}
