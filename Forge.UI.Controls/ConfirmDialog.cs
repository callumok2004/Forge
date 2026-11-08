using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class ConfirmDialog : Panel
{
	private const float MinWidth = 480f;
	private const float BoxPadding = 8f;
	private const float ToolbarHeight = 24f;
	private const float RowPaddingLeft = 12f;
	private const float RowPaddingRight = 8f;
	private const float IconSize = 24f;
	private const float TextGap = 12f;
	private const float MessageSize = 14f;
	private const float MessageMinHeight = 22f;
	private const float MessageGap = 4f;
	private const float DetailLineHeight = 20f;
	private const float FooterTop = 20f;
	private const float ButtonHeight = 26f;
	private const float ButtonGap = 10f;
	private const float ButtonPadding = 12f;
	private const float Radius = 8f;
	private const float MaxTextWidth = 600f;

	private readonly string _message;
	private readonly string _detail;
	private readonly Codicon _icon;
	private readonly Color _iconColor;
	private readonly Action<bool> _closed;
	private readonly Button _primary;
	private readonly Button _cancel;
	private readonly ActionButton _close;
	private readonly List<string> _detailLines = [];
	private float _wrappedWidth = -1f;
	private bool _done;

	private ConfirmDialog(string message, string detail, string primary, Codicon icon, Color iconColor, Action<bool> closed) {
		_message = message;
		_detail = detail;
		_icon = icon;
		_iconColor = iconColor;
		_closed = closed;
		Name = "ConfirmDialog";
		KeyboardInputEnabled = true;

		_primary = new PrimaryButton { Name = "DialogPrimary", Padding = new Thickness(ButtonPadding, 0f, ButtonPadding, 0f), SizeToContents = true, Action = () => Finish(true) };
		_primary.SetText(primary);
		AddChild(_primary);

		_cancel = new Button {
			Name = "DialogCancel",
			Background = Theme.ButtonSecondaryBackground,
			HoverBackground = Theme.ButtonSecondaryHoverBackground,
			Foreground = Theme.ButtonSecondaryForeground,
			Radius = Metrics.RadiusSmall,
			Padding = new Thickness(ButtonPadding, 0f, ButtonPadding, 0f),
			SizeToContents = true,
			Action = () => Finish(false),
		};
		_cancel.SetText("Cancel");
		AddChild(_cancel);

		_close = new ActionButton { Name = "DialogClose", Icon = Codicon.Close, Foreground = Theme.Foreground, HoverBackground = Theme.ListHoverBackground, Radius = Metrics.RadiusSmall, Action = () => Finish(false) };
		AddChild(_close);
	}

	public static void Show(string message, string detail, string primary, Action confirmed, Codicon icon = Codicon.Warning) =>
		Show(message, detail, primary, ok => {
			if (ok) confirmed();
		}, icon);

	public static void Show(string message, string detail, string primary, Action<bool> closed, Codicon icon = Codicon.Warning) {
		ConfirmDialog dialog = new(message, detail, primary, icon, icon == Codicon.Error ? Theme.ErrorForeground : icon == Codicon.Info ? Theme.InfoForeground : Theme.WarningForeground, closed);
		dialog.MakePopup(modal: true);
		dialog.RequestFocus();
	}

	public override bool DismissOnOutsideClick => false;

	public override bool DismissOnFocusLost => false;

	private void Finish(bool confirmed) {
		if (_done) return;

		_done = true;
		ClosePopup();
		_closed(confirmed);
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		const uint Enter = 0x0D;
		const uint Escape = 0x1B;

		if (key == Enter) {
			Finish(true);
			return true;
		}

		if (key == Escape) {
			Finish(false);
			return true;
		}

		return true;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) => true;

	private float TextLeft(float scale) => (BoxPadding + RowPaddingLeft + IconSize + TextGap) * scale;

	private void Wrap(ITextFont font, float width) {
		if (width == _wrappedWidth) return;

		_wrappedWidth = width;
		_detailLines.Clear();

		foreach (string paragraph in _detail.Split('\n')) {
			string line = string.Empty;

			foreach (string word in paragraph.Split(' ')) {
				string candidate = line.Length == 0 ? word : line + " " + word;

				if (line.Length > 0 && font.Measure(candidate) > width) {
					_detailLines.Add(line);
					line = word;
				}
				else {
					line = candidate;
				}
			}

			_detailLines.Add(line);
		}
	}

	public override void PerformLayout() {
		if (Ui is not { } ui) return;

		float scale = Scale;
		ITextFont message = ui.GetFonts().GetUi(MessageSize, true);
		ITextFont body = ui.GetFonts().GetUi(13f);
		float textWidth = MathF.Min(MaxTextWidth * scale, MathF.Max(message.Measure(_message), body.Measure(_detail)));
		float wide = MathF.Max(MinWidth * scale, TextLeft(scale) + textWidth + ((RowPaddingRight + BoxPadding) * scale));
		wide = MathF.Min(wide, ui.GetViewportWidth() * 0.9f);

		Wrap(body, wide - TextLeft(scale) - ((RowPaddingRight + BoxPadding) * scale));

		float messageHeight = MathF.Max(MessageMinHeight * scale, message.GetLineHeight());
		float detailHeight = _detail.Length == 0 ? 0f : _detailLines.Count * DetailLineHeight * scale;
		float buttonHeight = ButtonHeight * scale;
		float tall = ((BoxPadding * 2f) + ToolbarHeight + MessageGap + FooterTop) * scale + messageHeight + detailHeight + buttonHeight;

		SetBounds(MathF.Round((ui.GetViewportWidth() - wide) / 2f), MathF.Round((ui.GetViewportHeight() - tall) / 2f), wide, tall);

		float closeSize = 20f * scale;
		_close.SetBounds(wide - (BoxPadding * scale) - closeSize, BoxPadding * scale, closeSize, closeSize);

		_primary.FitContents();
		_cancel.FitContents();

		float right = wide - ((BoxPadding + RowPaddingRight) * scale);
		float top = tall - (BoxPadding * scale) - buttonHeight;
		_cancel.SetBounds(right - _cancel.Wide, top, _cancel.Wide, buttonHeight);
		_primary.SetBounds(right - _cancel.Wide - (ButtonGap * scale) - _primary.Wide, top, _primary.Wide, buttonHeight);
	}

	public override void PaintBackground(IPainter painter) {
		if (Ui is { } ui) painter.FillRectangle(0f, 0f, ui.GetViewportWidth(), ui.GetViewportHeight(), new Color(0f, 0f, 0f, 0.3f));

		float scale = Scale;
		float radius = Radius * scale;
		painter.FillRoundedRectangle(ScreenX + (2f * scale), ScreenY + (4f * scale), Wide, Tall, radius, Theme.WidgetShadow);
		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.EditorWidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX + (scale / 2f), ScreenY + (scale / 2f), Wide - scale, Tall - scale, radius, scale, Theme.EditorWidgetBorder);
	}

	public override void Paint(IPainter painter) {
		if (Ui is not { } ui) return;

		float scale = Scale;
		ITextFont message = ui.GetFonts().GetUi(MessageSize, true);
		ITextFont body = ui.GetFonts().GetUi(13f);
		float top = ScreenY + ((BoxPadding + ToolbarHeight) * scale);
		float messageHeight = MathF.Max(MessageMinHeight * scale, message.GetLineHeight());
		float iconSize = IconSize * scale;

		painter.DrawIcon(_icon, ScreenX + ((BoxPadding + RowPaddingLeft) * scale) + (iconSize / 2f), top + (messageHeight / 2f), iconSize, _iconColor);

		float left = ScreenX + TextLeft(scale);
		painter.DrawText(message, _message, left, top + ((messageHeight - message.GetLineHeight()) / 2f) + message.GetBaseline(), Theme.Foreground);

		float lineHeight = DetailLineHeight * scale;
		float y = top + messageHeight + (MessageGap * scale);

		foreach (string line in _detailLines) {
			if (_detail.Length == 0) break;

			painter.DrawText(body, line, left, y + ((lineHeight - body.GetLineHeight()) / 2f) + body.GetBaseline(), Theme.Foreground);
			y += lineHeight;
		}
	}
}
