using Forge.Common.Render;
using Forge.Theming;

namespace Forge.UI.Controls;

public class ActionButton : Button
{
	private const float BadgeHeight = 14f;

	public Codicon Icon {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public IVectorIcon? VectorIcon { get; set; }

	public float IconSize { get; set; } = Metrics.IconSize;

	public bool Checked {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public Color CheckedBackground { get; set; } = Theme.ActivityBarActiveBackground;

	public Color CheckedForeground { get; set; } = Theme.ActivityBarActiveForeground;

	public float CheckedRadius { get; set; } = Metrics.RadiusSmall;

	public int BadgeCount {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public override void PaintBackground(IPainter painter) {
		if (Checked) {
			painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, CheckedRadius * Scale, CheckedBackground);
			return;
		}

		base.PaintBackground(painter);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float size = IconSize * scale;
		float centreX = ScreenX + (Wide / 2f);
		float centreY = ScreenY + (Tall / 2f);
		Color color = !Enabled ? Foreground.WithAlpha(0.35f) : Checked ?CheckedForeground : IsCursorOver && HoverForeground is { } hover ? hover : Foreground;

		if (VectorIcon is { } vector) painter.DrawVectorIcon(vector, centreX, centreY, size, color);
		else painter.DrawIcon(Icon, centreX, centreY, size, color);

		if (BadgeCount > 0) PaintBadge(painter, scale);
	}

	private void PaintBadge(IPainter painter, float scale) {
		ITextFont font = painter.GetUiSmall();
		Span<char> text = stackalloc char[8];
		int length = BadgeCount > 99 ? Write(text, "99+") : BadgeCount.TryFormat(text, out int digits) ? digits : 0;

		float height = BadgeHeight * scale;
		float width = MathF.Max(font.Measure(text[..length]) + (6f * scale), height);
		float right = ScreenX + Wide - (2f * scale);
		float bottom = ScreenY + Tall - scale;

		painter.FillRoundedRectangle(right - width, bottom - height, width, height, height / 2f, Theme.ActivityBarBadgeBackground);
		painter.DrawText(font, text[..length], right - width + ((width - font.Measure(text[..length])) / 2f), bottom - height + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline(), Theme.ActivityBarBadgeForeground);
	}

	private static int Write(Span<char> destination, ReadOnlySpan<char> text) {
		text.CopyTo(destination);
		return text.Length;
	}
}
