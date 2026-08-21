using Forge.Common.Render;
using Forge.Theming;

namespace Forge.UI.Controls;

public static class TextBox
{
	public static void Draw(IPainter painter, ReadOnlySpan<char> text, ReadOnlySpan<char> placeholder, float left, float top, float width, float height, float scale, bool focused) {
		ITextFont font = painter.GetUi();
		float radius = Metrics.RadiusSmall * scale;

		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.EditorBackground);

		painter.StrokeRoundedRectangle(left, top, width, height, radius, Metrics.StrokeThickness * scale, focused ? Theme.Accent : Theme.WidgetBorder);

		float textLeft = left + (Metrics.Space6 * scale);
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.PushClip(textLeft, top, Math.Max(0f, width - (Metrics.Space12 * scale)), height);

		if (text.IsEmpty && !focused) painter.DrawText(font, placeholder, textLeft, baseline, Theme.SectionHeaderForeground);
		else painter.DrawText(font, text, textLeft, baseline, Theme.EditorForeground);

		if (focused && Caret.Visible) {
			painter.FillRectangle(textLeft + font.Measure(text), baseline - font.GetBaseline() + (2f * scale), Metrics.StrokeThickness * scale, font.GetLineHeight() - (4f * scale), Theme.CursorForeground);
		}

		painter.PopClip();
	}
}
