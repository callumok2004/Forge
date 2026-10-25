using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Language;

public sealed class SignatureHelpWidget : Panel
{
	private const float Padding = 8f;
	private const float MaxWidth = 720f;

	private SignatureHelp? _help;
	private int _overload;

	public SignatureHelpWidget() {
		Name = "SignatureHelp";
		Visible = false;
		MouseInputEnabled = false;
	}

	public bool IsOpen => _help is not null;

	public int Overload => _overload;

	public bool HasOverloads => _help is { OverloadCount: > 1 };

	public void Show(SignatureHelp help) {
		if (_help?.Label != help.Label || _overload >= help.OverloadCount) _overload = help.OverloadIndex;

		_help = help;
	}

	public void Hide() => _help = null;

	public void Cycle(int delta) {
		if (_help is { OverloadCount: > 1 } help) _overload = (((_overload + delta) % help.OverloadCount) + help.OverloadCount) % help.OverloadCount;
	}

	private float CountWidth(ITextFont ui, SignatureHelp help) {
		if (help.OverloadCount <= 1) return 0f;

		Span<char> buffer = stackalloc char[24];
		return ui.Measure(FormatCount(buffer, help)) + (Metrics.Space8 * Scale);
	}

	private ReadOnlySpan<char> FormatCount(Span<char> buffer, SignatureHelp help) {
		(_overload + 1).TryFormat(buffer, out int written);
		buffer[written++] = '/';
		help.OverloadCount.TryFormat(buffer[written..], out int digits);
		return buffer[..(written + digits)];
	}

	public void Place(float caretX, float caretTop, float caretHeight, float areaTop, float areaBottom, float areaRight, float originX, float originY) {
		SignatureHelp? help = _help;
		Visible = help is not null;

		if (help is null || Ui is null) return;

		float scale = Scale;
		IFontSet fonts = Ui.GetFonts();
		ITextFont font = fonts.GetCode();
		float padding = Padding * scale;
		float width = Math.Min(MaxWidth * scale, font.Measure(help.Label) + CountWidth(fonts.GetUi(), help) + (padding * 2f));
		float height = font.GetLineHeight() + (padding * 2f);
		float top = caretTop - height - (Metrics.Space2 * scale);

		if (top < areaTop) top = Math.Min(caretTop + caretHeight, areaBottom - height);

		float left = Math.Max(0f, Math.Min(caretX, areaRight - width));
		SetBounds(left - originX, top - originY, width, height);
	}

	public override void Paint(IPainter painter) {
		if (_help is not { } help) return;

		float scale = Scale;
		ITextFont font = painter.GetCode();
		ITextFont ui = painter.GetUi();
		float padding = Padding * scale;
		float countWidth = CountWidth(ui, help);
		float radius = Metrics.RadiusMedium * scale;
		float baseline = ScreenY + padding + font.GetBaseline();

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		painter.PushClip(ScreenX, ScreenY, Wide - countWidth, Tall);
		painter.DrawText(font, help.Label, ScreenX + padding, baseline, Theme.SectionHeaderForeground);

		if (help.ParameterIndex >= 0 && help.ParameterIndex < help.Parameters.Count) {
			(int start, int length) = help.Parameters[help.ParameterIndex];

			if (length > 0 && start + length <= help.Label.Length) painter.DrawText(font, help.Label.AsSpan(start, length), ScreenX + padding + font.Measure(help.Label.AsSpan(0, start)), baseline, Theme.EditorForeground);
		}

		painter.PopClip();

		if (help.OverloadCount <= 1) return;

		Span<char> buffer = stackalloc char[24];
		ReadOnlySpan<char> count = FormatCount(buffer, help);
		painter.DrawText(ui, count, ScreenX + Wide - padding - ui.Measure(count), ScreenY + ((Tall - ui.GetLineHeight()) / 2f) + ui.GetBaseline(), Theme.SectionHeaderForeground);
	}
}
