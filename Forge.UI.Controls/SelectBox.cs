using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class SelectBox : Panel
{
	private const float PaddingX = 8f;
	private const float ChevronSlot = 23f;
	private const float ChevronSize = 16f;
	private const float ChevronInset = 4f;

	private readonly Menu _menu = new();
	private string[] _options;
	private long _closedAt;

	public SelectBox(params string[] options) {
		_options = options;
		Name = "Select";
		Cursor = CursorCode.Hand;
		_menu.Closed = () => {
			_closedAt = Environment.TickCount64;
			Repaint();
		};
	}

	public float FontSize { get; init; } = Metrics.FontSizeBody;

	public int Selected {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public Action<int>? Changed { get; set; }

	public string Text => (uint)Selected < (uint)_options.Length ? _options[Selected] : string.Empty;

	public void SetOptions(string[] options, int selected) {
		_options = options;
		Selected = Math.Clamp(selected, 0, Math.Max(0, options.Length - 1));
		Parent?.InvalidateLayout();
		Repaint();
	}

	public float PreferredWidth(float scale) {
		if (Ui is null) return 80f * scale;

		ITextFont font = Ui.GetFonts().GetUi(FontSize);
		float widest = 0f;

		foreach (string option in _options) widest = MathF.Max(widest, font.Measure(option));

		return widest + ((PaddingX + ChevronSlot) * scale);
	}

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() => Repaint();

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || !Enabled || _options.Length == 0) return button == MouseButton.Left;

		if (_menu.IsOpen || Environment.TickCount64 - _closedAt < 150) {
			_menu.Close();
			return true;
		}

		_menu.Clear();

		for (int i = 0; i < _options.Length; i++) {
			int index = i;
			_menu.Add(new MenuItem(_options[i], action: () => Choose(index)) { Checked = i == Selected });
		}

		(float absoluteX, float absoluteY) = GetAbsPos();
		_menu.Open(absoluteX, absoluteY + Tall + (2f * Scale));
		Repaint();
		return true;
	}

	private void Choose(int index) {
		if (index == Selected) return;

		Selected = index;
		Changed?.Invoke(index);
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		int next = key switch {
			Forge.Core.Native.VirtualKey.Down => Math.Min(_options.Length - 1, Selected + 1),
			Forge.Core.Native.VirtualKey.Up => Math.Max(0, Selected - 1),
			_ => -1,
		};

		if (next < 0) return false;

		Choose(next);
		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusSmall * scale;
		ITextFont font = painter.GetUi(FontSize);
		bool active = _menu.IsOpen;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.DropdownBackground);
		float stroke = Metrics.StrokeThickness * scale;
		painter.StrokeRoundedRectangle(ScreenX + (stroke / 2f), ScreenY + (stroke / 2f), Wide - stroke, Tall - stroke, radius, stroke, active || HasFocus ? Theme.FocusBorder : Theme.DropdownBorder);

		painter.PushClip(ScreenX, ScreenY, MathF.Max(0f, Wide - (ChevronSlot * scale)), Tall);
		painter.DrawText(font, Text, ScreenX + (PaddingX * scale), ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline(), Enabled ? Theme.DropdownForeground : Theme.DisabledForeground);
		painter.PopClip();
		painter.DrawIcon(Codicon.ChevronDown, ScreenX + Wide - ((ChevronInset + (ChevronSize / 2f)) * scale), ScreenY + (Tall / 2f), ChevronSize * scale, Enabled ? Theme.DropdownForeground : Theme.DisabledForeground);
	}
}
