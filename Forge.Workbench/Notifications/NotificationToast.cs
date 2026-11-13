using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class NotificationToast : Panel
{
	public const float Width = 450f;
	private const float MessageHeight = 42f;
	private const float LineHeight = 22f;
	private const float VerticalPadding = 10f;
	private const float ButtonRowHeight = 34f;
	private const float ButtonHeight = 24f;
	private const float IconSize = 16f;
	private const float CloseSize = 22f;

	private readonly string _text;
	private readonly List<(int Start, int Length)> _lines = [];
	private float _wrappedWidth = -1f;
	private readonly ActionButton _close = new() { Icon = Codicon.Close, Visible = false };
	private readonly List<PrimaryButton> _buttons = [];
	private readonly MessageSeverity _severity;

	public NotificationToast(MessageSeverity severity, string message, IReadOnlyList<string> actions, Action<int>? chosen) {
		_severity = severity;
		Name = "NotificationToast";

		_text = message;
		_close.Action = () => Dismissed?.Invoke(this);
		_close.CursorExited = OnCursorExited;

		AddChild(_close);

		_chosen = chosen;

		for (int i = 0; i < actions.Count; i++) {
			int index = i;
			PrimaryButton button = new() { Action = () => {
				Resolve(index);
				Dismissed?.Invoke(this);
			}, CursorExited = OnCursorExited };

			button.SetText(actions[i]);
			_buttons.Add(button);
			AddChild(button);
		}
	}

	private Action<int>? _chosen;

	public Action<NotificationToast>? Dismissed { get; set; }

	public void Resolve(int index) {
		Action<int>? chosen = _chosen;
		_chosen = null;
		chosen?.Invoke(index);
	}

	public bool Sticky => _severity == MessageSeverity.Error || _buttons.Count > 0;

	public float PreferredHeight => MessageAreaHeight() + ((_buttons.Count > 0 ? ButtonRowHeight : 0f) * Scale);

	private float TextWidth(float scale) => (Width * scale) - ((IconSize + Metrics.Space8 + Metrics.Space8) * scale) - (CloseSize * scale) - (Metrics.Space8 * 2f * scale);

	private float MessageAreaHeight() {
		float scale = Scale;
		Wrap(TextWidth(scale));
		return MathF.Max(MessageHeight, (_lines.Count * LineHeight) + (VerticalPadding * 2f)) * scale;
	}

	private void Wrap(float width) {
		if (width == _wrappedWidth || Ui is null) return;

		_wrappedWidth = width;
		_lines.Clear();

		ITextFont font = Ui.GetFonts().GetUi();
		ReadOnlySpan<char> text = _text;
		int start = 0;

		while (start < text.Length) {
			int newline = text[start..].IndexOfAny('\r', '\n');
			int paragraphEnd = newline < 0 ? text.Length : start + newline;
			int lineStart = start;

			while (lineStart < paragraphEnd) {
				int end = paragraphEnd;

				if (font.Measure(text[lineStart..end]) > width) {
					int fit = lineStart + 1;

					for (int i = lineStart + 1; i <= paragraphEnd; i++) {
						if (font.Measure(text[lineStart..i]) > width) break;

						fit = i;
					}

					int space = text[lineStart..fit].LastIndexOf(' ');
					end = space > 0 && fit < paragraphEnd ? lineStart + space : fit;
				}

				_lines.Add((lineStart, end - lineStart));
				lineStart = end;

				while (lineStart < paragraphEnd && text[lineStart] == ' ') lineStart++;
			}

			if (paragraphEnd == start) _lines.Add((start, 0));

			start = paragraphEnd;

			if (start < text.Length && text[start] == '\r') start++;
			if (start < text.Length && text[start] == '\n') start++;
		}

		if (_lines.Count == 0) _lines.Add((0, 0));
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi();
		float left = ScreenX + ((IconSize + Metrics.Space8 + Metrics.Space8) * scale);
		float area = MessageAreaHeight();
		float block = _lines.Count * LineHeight * scale;
		float top = ScreenY + ((area - block) / 2f);
		float lineHeight = LineHeight * scale;

		painter.PushClip(left, ScreenY, TextWidth(scale), area);

		for (int i = 0; i < _lines.Count; i++) {
			(int start, int length) = _lines[i];
			painter.DrawText(font, _text.AsSpan(start, length), left, top + (i * lineHeight) + ((lineHeight - font.GetLineHeight()) / 2f) + font.GetBaseline(), Theme.Foreground);
		}

		painter.PopClip();
	}

	public override void OnCursorEntered() => SetCloseVisible(true);

	public override void OnCursorExited() => SetCloseVisible(ContainsCursor());

	private bool ContainsCursor() => Ui?.GetMouseOver() is { } over && (ReferenceEquals(over, this) || over.HasParent(this));

	private void SetCloseVisible(bool visible) {
		if (_close.Visible == visible) return;

		_close.Visible = visible;
		Repaint();
	}

	public override void PerformLayout() {
		float scale = Scale;
		float inset = Metrics.Space8 * scale;
		float icon = (IconSize + Metrics.Space8 + Metrics.Space8) * scale;
		float closeSize = CloseSize * scale;

		float area = MessageAreaHeight();

		_close.SetBounds(Wide - inset - closeSize, ((VerticalPadding + (LineHeight / 2f)) * scale) - (closeSize / 2f), closeSize, closeSize);

		float right = Wide - inset;
		float top = area + (((ButtonRowHeight - ButtonHeight) / 2f - Metrics.Space4) * scale);

		for (int i = _buttons.Count - 1; i >= 0; i--) {
			PrimaryButton button = _buttons[i];
			float wide = button.MeasureContents(Ui!.GetFonts().GetUi(), scale) + (Metrics.Space8 * 2f * scale);

			right -= wide;
			button.SetBounds(right, top, wide, ButtonHeight * scale);
			right -= inset;
		}
	}

	public override void PaintBackground(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusLarge * scale;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		(Codicon glyph, Color color) = _severity switch {
			MessageSeverity.Error => (Codicon.Error, Theme.ErrorForeground),
			MessageSeverity.Warning => (Codicon.Warning, Theme.WarningForeground),
			_ => (Codicon.Info, Theme.InfoForeground),
		};

		painter.DrawIcon(glyph, ScreenX + ((Metrics.Space8 + (IconSize / 2f)) * scale) + (Metrics.Space4 * scale), ScreenY + ((VerticalPadding + (LineHeight / 2f)) * scale), IconSize * scale, color);
	}
}
