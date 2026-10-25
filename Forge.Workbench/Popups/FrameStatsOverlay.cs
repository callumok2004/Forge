using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Workbench;

public sealed class FrameStatsOverlay : Panel
{
	public const float Width = 520f;
	public const float BadgeHeight = 22f;

	public FrameStatsOverlay() {
		Name = "FrameStats";
		Visible = false;
		MouseInputEnabled = false;
	}

	public IFrameStats? Stats { get; set; }

	public override void Paint(IPainter painter) {
		if (Stats is not { } stats) return;

		Span<char> buffer = stackalloc char[64];
		float right = ScreenX + Wide;

		int length = FormatTriple(buffer, "drawn ", stats.GetLastDamagePercent(), stats.GetAverageDamagePercent(), stats.GetPeakDamagePercent(), "%", "F0");
		right = DrawBadge(painter, buffer[..length], right);

		length = Write(buffer, "fps ");
		((int)stats.GetImpliedFps()).TryFormat(buffer[length..], out int digits);
		right = DrawBadge(painter, buffer[..(length + digits)], right);

		length = FormatTriple(buffer, "", stats.GetLastFrameMs(), stats.GetAverageFrameMs(), stats.GetPeakFrameMs(), " ms", "F2");
		DrawBadge(painter, buffer[..length], right);
	}

	private float DrawBadge(IPainter painter, ReadOnlySpan<char> text, float right) {
		float scale = Scale;
		ITextFont font = painter.GetUiSmall();
		float padding = Metrics.SpaceMd * scale;
		float width = font.Measure(text) + (padding * 2f);
		float x = right - width;

		painter.FillRoundedRectangle(x, ScreenY, width, Tall, Metrics.CornerRadius * scale, Theme.WidgetBackground);
		painter.DrawText(font, text, x + padding, ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline(), Theme.AccentMuted);

		return x - (Metrics.SpaceSm * scale);
	}

	private static int FormatTriple(Span<char> buffer, ReadOnlySpan<char> label, double last, double average, double peak, ReadOnlySpan<char> unit, ReadOnlySpan<char> format) {
		int written = Write(buffer, label);

		last.TryFormat(buffer[written..], out int digits, format);
		written += digits;
		written += Write(buffer[written..], " / ");
		average.TryFormat(buffer[written..], out digits, format);
		written += digits;
		written += Write(buffer[written..], " / ");
		peak.TryFormat(buffer[written..], out digits, format);
		written += digits;

		return written + Write(buffer[written..], unit);
	}

	private static int Write(Span<char> destination, ReadOnlySpan<char> text) {
		text.CopyTo(destination);
		return text.Length;
	}
}
