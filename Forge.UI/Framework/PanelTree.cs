using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.UI.Framework;

public sealed class PanelTree : Panel
{
	private const float PreferredWidth = 460f;
	private const float HeaderHeight = 28f;
	private const int RefreshMilliseconds = 250;
	private const int AutoExpandDepth = 3;

	private readonly UiSystem _system;
	private readonly ListPanel _list = new() { Dock = DockType.Fill, RowHeight = 18f };
	private readonly List<(Panel Panel, int Depth)> _rows = [];
	private readonly HashSet<Panel> _toggled = [with(ReferenceEqualityComparer.Instance)];

	public PanelTree(UiSystem system) {
		_system = system;
		Name = "PanelTree";
		DockPadding = new Thickness(0f, HeaderHeight, 0f, 4f);
		AddChild(_list);

		_list.RowPainter = PaintRow;
		_list.Hovered = row => _system.Highlighted = row >= 0 && row < _rows.Count ? _rows[row].Panel : null;
		_list.Activated = Toggle;
	}

	private bool IsExpanded(Panel panel, int depth) => (depth < AutoExpandDepth) != _toggled.Contains(panel);

	private void Toggle(int row) {
		if (row < 0 || row >= _rows.Count || _rows[row].Panel.Children.Count == 0) return;

		Panel panel = _rows[row].Panel;

		if (!_toggled.Remove(panel)) _toggled.Add(panel);

		Collect();
		_list.Repaint();
	}

	public bool IsOpen => IsPopup;

	public void Toggle() {
		if (IsPopup) {
			RemoveTickSignal();
			_toggled.Clear();
			_system.Highlighted = null;
			ClosePopup();
			return;
		}

		Collect();
		MakePopup();
		AddTickSignal(RefreshMilliseconds);
	}

	public override bool DismissOnOutsideClick => false;

	public override void OnPopupDismissed() => Toggle();

	public override void OnTick() {
		Collect();
		_list.Repaint();
	}

	private void Collect() {
		_rows.Clear();
		Walk(_system.Root, 0);

		foreach (Panel popup in _system.Popups) Walk(popup, 0);

		_list.SetCount(_rows.Count);
	}

	private void Walk(Panel panel, int depth) {
		_rows.Add((panel, depth));

		if (!IsExpanded(panel, depth)) return;

		foreach (Panel child in panel.Children) Walk(child, depth + 1);
	}

	public override void PerformLayout() {
		float scale = Scale;
		float margin = Metrics.Space16 * scale;
		float top = Metrics.TitleBarHeight * scale;
		float wide = PreferredWidth * scale;

		SetBounds(_system.GetViewportWidth() - wide - margin, top, wide, _system.GetViewportHeight() - top - margin);
		DockPadding = new Thickness(0f, HeaderHeight * scale, 0f, 4f * scale);
		base.PerformLayout();
	}

	public override void PaintBackground(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusLarge * scale;
		ITextFont font = painter.GetUiStrong();

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
		painter.DrawText(font, "forgeui_drawtree", ScreenX + (Metrics.Space12 * scale), ScreenY + (((HeaderHeight * scale) - font.GetLineHeight()) / 2f) + font.GetBaseline(), Theme.Foreground);
	}

	private void PaintRow(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		if (index >= _rows.Count) return;

		(Panel panel, int depth) = _rows[index];
		ITextFont font = painter.GetUiSmall();
		float scale = Scale;
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float indent = left + (Metrics.Space12 * scale) + (depth * Metrics.Space12 * scale);
		float twistie = Metrics.IconSize * scale;
		float x = indent + twistie;

		if (panel.Children.Count > 0) painter.DrawIcon(IsExpanded(panel, depth) ? Codicon.ChevronDown : Codicon.ChevronRight, indent + (twistie / 2f), top + (height / 2f), twistie, Theme.SectionHeaderForeground);

		Span<char> buffer = stackalloc char[160];
		int written = 0;

		Append(buffer, ref written, panel.Name ?? panel.GetType().Name);
		Append(buffer, ref written, "  ");
		Number(buffer, ref written, panel.X);
		Append(buffer, ref written, ",");
		Number(buffer, ref written, panel.Y);
		Append(buffer, ref written, " ");
		Number(buffer, ref written, panel.Wide);
		Append(buffer, ref written, "x");
		Number(buffer, ref written, panel.Tall);
		Append(buffer, ref written, panel.Visible ? "  V" : "  -");
		Append(buffer, ref written, panel.MouseInputEnabled ? "M" : "-");
		Append(buffer, ref written, panel.KeyboardInputEnabled ? "K" : "-");
		Append(buffer, ref written, panel.IsPopup ? "P" : "-");
		Append(buffer, ref written, panel.HasFocus ? "F" : "-");

		Theming.Color color = !panel.IsFullyVisible() ? Theme.DisabledForeground : ReferenceEquals(panel, _system.GetMouseOver()) ? Theme.ListHighlightForeground : Theme.Foreground;
		painter.DrawText(font, buffer[..written], x, baseline, color);
	}

	private static void Append(Span<char> buffer, ref int written, ReadOnlySpan<char> text) {
		int count = Math.Min(text.Length, buffer.Length - written);
		text[..count].CopyTo(buffer[written..]);
		written += count;
	}

	private static void Number(Span<char> buffer, ref int written, float value) {
		if (((int)value).TryFormat(buffer[written..], out int digits)) written += digits;
	}
}
