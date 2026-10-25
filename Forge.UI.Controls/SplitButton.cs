using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class SplitButton : Panel
{
	private const float DropdownWidth = 24f;
	private const float IconSize = 16f;

	private readonly Label _label = new() { Name = "SplitButtonLabel", Alignment = TextAlignment.Center, Foreground = Theme.ButtonForeground, IconColor = static _ => Theme.ButtonForeground };
	private bool _hoverDropdown;
	private bool _pressed;

	public SplitButton() {
		Cursor = CursorCode.Arrow;
		AddChild(_label);
	}

	public string? Command { get; set; }

	public Action? Action { get; set; }

	public Func<Menu>? DropdownMenu { get; set; }

	public void SetText(ReadOnlySpan<char> text) => _label.SetText(text);

	private float DropdownLeft => Wide - (DropdownMenu is null ? 0f : DropdownWidth * Scale);

	public override void PerformLayout() => _label.SetBounds(0f, 0f, DropdownLeft, Tall);

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() {
		_pressed = false;
		_hoverDropdown = false;
		Repaint();
	}

	public override void OnCursorMoved(float x, float y) {
		bool dropdown = DropdownMenu is not null && x >= DropdownLeft;

		if (dropdown == _hoverDropdown) return;

		_hoverDropdown = dropdown;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || !Enabled) return true;

		_pressed = true;
		Repaint();
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_pressed) return true;

		_pressed = false;
		Repaint();

		if (x < 0f || y < 0f || x >= Wide || y >= Tall) return true;

		if (DropdownMenu is { } factory && x >= DropdownLeft) {
			(float absX, float absY) = GetAbsPos();
			factory().Open(absX + DropdownLeft, absY + Tall);
		}
		else if (Action is { } action) action();
		else if (Command is { } command) PostCommand(command);

		return true;
	}

	public override void PaintBackground(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusSmall * scale;
		Color background = !Enabled ? Theme.ButtonBackground.WithAlpha(0.5f) : IsCursorOver ? Theme.ButtonHoverBackground : Theme.ButtonBackground;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, background);

		if (_pressed && Enabled) painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.PressedOverlay);

		if (DropdownMenu is null) return;

		float left = ScreenX + DropdownLeft;
		float inset = 4f * scale;

		painter.DrawVerticalLine(MathF.Round(left), ScreenY + inset, ScreenY + Tall - inset, Theme.ButtonForeground.WithAlpha(0.4f));
		painter.DrawIcon(Codicon.ChevronDown, left + (DropdownWidth * scale / 2f), ScreenY + (Tall / 2f), IconSize * scale, Theme.ButtonForeground);
	}
}
