using Forge.Common.Render;
using Forge.Theming;

namespace Forge.UI.Controls;

public static class ButtonPainter
{
	public static void Draw(IPainter painter, ReadOnlySpan<char> label, Codicon icon, float left, float top, float width, float height, float scale, bool enabled, bool hovered, bool pressed) {
		ITextFont font = painter.GetUi();

		Color background = enabled ? Theme.Accent : Theme.WidgetBackground;
		Color foreground = enabled ? Theme.TabActiveForeground : Theme.SectionHeaderForeground;

		painter.FillRoundedRectangle(left, top, width, height, Metrics.RadiusSmall * scale, background);

		if (enabled && (pressed || hovered)) {
			painter.FillRoundedRectangle(left, top, width, height, Metrics.RadiusSmall * scale, pressed ? Theme.PressedOverlay : Theme.HoverOverlay);
		}

		float iconSize = Metrics.IconSize * scale;
		float gap = Metrics.Space6 * scale;
		float pairWidth = font.Measure(label) + (icon == default ? 0f : iconSize + gap);
		float pairLeft = left + ((width - pairWidth) / 2f);

		if (icon != default) {
			painter.DrawIcon(icon, pairLeft + (iconSize / 2f), top + (height / 2f), iconSize, foreground);
			pairLeft += iconSize + gap;
		}

		painter.DrawText(font, label, pairLeft, top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline(), foreground);
	}
}
