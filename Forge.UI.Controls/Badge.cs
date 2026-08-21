using Forge.Common.Render;
using Forge.Theming;

namespace Forge.UI.Controls;

public static class Badge
{
	public const float Height = 16f;

	public static float Width(ITextFont font, int count, float scale) {
		if (count <= 0) return 0f;

		Span<char> digits = stackalloc char[8];

		return Math.Max(Height * scale, font.Measure(Format(count, digits)) + (Metrics.Space4 * 2f * scale));
	}

	public static float Draw(IPainter painter, int count, float left, float centreY, float scale, Color background, Color foreground) {
		if (count <= 0) return 0f;

		ITextFont font = painter.GetUiSmall();

		Span<char> digits = stackalloc char[8];
		ReadOnlySpan<char> label = Format(count, digits);

		float height = Height * scale;
		float width = Math.Max(height, font.Measure(label) + (Metrics.Space4 * 2f * scale));

		painter.FillRoundedRectangle(left, centreY - (height / 2f), width, height, height / 2f, background);

		painter.DrawText(font, label, left + ((width - font.Measure(label)) / 2f), centreY - (font.GetLineHeight() / 2f) + font.GetBaseline(), foreground);

		return width;
	}

	private static ReadOnlySpan<char> Format(int count, Span<char> digits) {
		if (count > 99) {
			"99+".CopyTo(digits);
			return digits[..3];
		}

		return count.TryFormat(digits, out int written) ? digits[..written] : default;
	}
}
