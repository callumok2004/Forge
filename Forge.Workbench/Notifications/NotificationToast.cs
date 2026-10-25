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
	private const float ButtonRowHeight = 34f;
	private const float ButtonHeight = 24f;
	private const float IconSize = 16f;
	private const float CloseSize = 22f;

	private readonly Label _message = new() { Font = FontRole.Ui };
	private readonly ActionButton _close = new() { Icon = Codicon.Close, Visible = false };
	private readonly List<PrimaryButton> _buttons = [];
	private readonly MessageSeverity _severity;

	public NotificationToast(MessageSeverity severity, string message, IReadOnlyList<string> actions, Action<int>? chosen) {
		_severity = severity;
		Name = "NotificationToast";

		_message.SetText(message);
		_close.Action = () => Dismissed?.Invoke(this);

		AddChild(_message);
		AddChild(_close);

		_chosen = chosen;

		for (int i = 0; i < actions.Count; i++) {
			int index = i;
			PrimaryButton button = new() { Action = () => {
				Resolve(index);
				Dismissed?.Invoke(this);
			} };

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

	public float PreferredHeight => (MessageHeight + (_buttons.Count > 0 ? ButtonRowHeight : 0f)) * Scale;

	public override void OnCursorEntered() => SetCloseVisible(true);

	public override void OnCursorExited() => SetCloseVisible(IsCursorOver);

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

		_close.SetBounds(Wide - inset - closeSize, ((MessageHeight * scale) - closeSize) / 2f, closeSize, closeSize);
		_message.SetBounds(icon, 0f, Wide - icon - closeSize - (inset * 2f), MessageHeight * scale);

		float right = Wide - inset;
		float top = (MessageHeight * scale) + (((ButtonRowHeight - ButtonHeight) / 2f - Metrics.Space4) * scale);

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

		painter.DrawIcon(glyph, ScreenX + ((Metrics.Space8 + (IconSize / 2f)) * scale) + (Metrics.Space4 * scale), ScreenY + (MessageHeight * scale / 2f), IconSize * scale, color);
	}
}
