using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class MenuItem : Panel
{
	public const float RowHeight = 22f;
	public const float SeparatorHeight = 7f;

	public MenuItem(string? label, string? command = null, Action? action = null, Menu? submenu = null) {
		Label = label;
		Command = command;
		Action = action;
		Submenu = submenu;
		Dock = DockType.Top;
		MouseInputEnabled = label is not null;
	}

	public static MenuItem Separator() => new(null);

	public string? Label { get; }

	public string? Command { get; }

	public Action? Action { get; }

	public Menu? Submenu { get; }

	public bool Checked { get; set; }

	public bool IsSeparator => Label is null;

	public bool IsEnabled => Submenu is not null || Action is not null || (Command is { } id && (Ui?.IsCommandEnabled(id) ?? true));

	private bool _keybindingResolved;
	private string? _keybinding;

	public string? Keybinding {
		get {
			if (_keybindingResolved) return _keybinding;

			_keybindingResolved = true;
			return _keybinding = Command is { } id && Ui?.GetKeybinding(id) is { } keys ? FirstAlternative(keys) : null;
		}
	}

	private static string FirstAlternative(string keys) {
		int split = keys.IndexOf('|');
		return split < 0 ? keys : keys[..split];
	}

	public float PreferredWidth(ITextFont font, float scale) {
		if (IsSeparator) return 0f;

		float trailing = Submenu is not null ? 28f * scale : Keybinding is { } keys ? (28f * scale) + font.Measure(keys) : 0f;
		return font.Measure(Label) + trailing + (Metrics.Space16 * 2f * scale) + (16f * scale);
	}

	public override void OnCursorEntered() => (Parent as Menu)?.Highlight(this, fromKeyboard: false);

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		(Parent as Menu)?.Activate(this);
		return true;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) => true;

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float padX = Metrics.Space16 * scale;

		if (IsSeparator) {
			painter.DrawHorizontalLine(ScreenY + (Tall / 2f), ScreenX + (Metrics.Space8 * scale), ScreenX + Wide - (Metrics.Space8 * scale), Theme.MenuSeparatorBackground);
			return;
		}

		bool highlighted = (Parent as Menu)?.Highlighted == this;
		bool enabled = IsEnabled;

		if (highlighted) {
			float inset = Metrics.Space4 * scale;
			painter.FillRoundedRectangle(ScreenX + inset, ScreenY, Wide - (inset * 2f), Tall, Metrics.RadiusSmall * scale, Theme.MenuSelectionBackground);
		}

		ITextFont font = painter.GetUi();
		float baseline = ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();
		Color color = !enabled ? Theme.DisabledForeground : highlighted ? Theme.MenuSelectionForeground : Theme.MenuForeground;

		if (Checked) painter.DrawIcon(Codicon.Check, ScreenX + (padX / 2f) + (2f * scale), ScreenY + (Tall / 2f), 14f * scale, color);

		painter.DrawText(font, Label, ScreenX + padX + (8f * scale), baseline, color);

		if (Submenu is not null) painter.DrawIcon(Codicon.ChevronRight, ScreenX + Wide - padX, ScreenY + (Tall / 2f), 14f * scale, color);
		else if (Keybinding is { } keys) painter.DrawText(font, keys, ScreenX + Wide - padX - font.Measure(keys), baseline, Theme.DescriptionForeground);
	}
}
