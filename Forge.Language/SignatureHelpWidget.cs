using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Editor;
using Forge.Theming;

namespace Forge.Language;

public sealed class SignatureHelpWidget
{
	private const float Padding = 8f;
	private const float MaxWidth = 720f;

	private SignatureHelp? _help;

	public bool IsOpen => _help is not null;

	private int _overload;

	public void Show(SignatureHelp help) {
		if (_help?.Label != help.Label || _overload >= help.OverloadCount) _overload = help.OverloadIndex;

		_help = help;
	}

	public void Hide() => _help = null;

	public int Overload => _overload;

	public bool HasOverloads => _help is { OverloadCount: > 1 };

	public void Cycle(int delta) {
		if (_help is { OverloadCount: > 1 } help) {
			_overload = ((_overload + delta) % help.OverloadCount + help.OverloadCount) %
				help.OverloadCount;
		}
	}

	public void Render(IPainter painter, float caretX, float caretTop, float caretHeight,
		float editorTop, float editorBottom, float editorRight, float scale) {
		if (_help is not { } help) return;

		ITextFont font = painter.GetCode();
		ITextFont ui = painter.GetUi();

		float padding = Padding * scale;
		float labelWidth = font.Measure(help.Label);

		ReadOnlySpan<char> count = help.OverloadCount > 1
			? $"{_overload + 1}/{help.OverloadCount}"
			: default;

		float countWidth = count.IsEmpty ? 0f : ui.Measure(count) + (Metrics.Space8 * scale);
		float width = Math.Min(MaxWidth * scale, labelWidth + countWidth + (padding * 2f));
		float height = font.GetLineHeight() + (padding * 2f);

		float top = caretTop - height - (Metrics.Space2 * scale);

		if (top < editorTop) top = Math.Min(caretTop + caretHeight, editorBottom - height);

		float left = Math.Max(0f, Math.Min(caretX, editorRight - width));
		float radius = Metrics.RadiusMedium * scale;

		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(left, top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		float baseline = top + padding + font.GetBaseline();

		painter.PushClip(left, top, width - countWidth, height);
		painter.DrawText(font, help.Label, left + padding, baseline, Theme.SectionHeaderForeground);

		if (help.ParameterIndex >= 0 && help.ParameterIndex < help.Parameters.Count) {
			(int start, int length) = help.Parameters[help.ParameterIndex];

			if (length > 0 && start + length <= help.Label.Length) {
				float offset = font.Measure(help.Label.AsSpan(0, start));

				painter.DrawText(font, help.Label.AsSpan(start, length), left + padding + offset,
					baseline, Theme.EditorForeground);
			}
		}

		painter.PopClip();

		if (!count.IsEmpty) {
			float countBaseline = top + ((height - ui.GetLineHeight()) / 2f) + ui.GetBaseline();

			painter.DrawText(ui, count, left + width - padding - ui.Measure(count), countBaseline,
				Theme.SectionHeaderForeground);
		}
	}
}
