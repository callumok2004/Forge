using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Terminal;
using Forge.Theming;

namespace Forge.Workbench;

public sealed class TerminalView : Panel
{
	private const float ListWidth = 150f;

	private readonly PanelPart _part;
	private readonly TerminalList _list;
	private readonly TerminalScreenPanel _screen;

	public TerminalView(PanelPart part) {
		_part = part;
		Name = "Terminal";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_list = new TerminalList(part) { Dock = DockType.Right, Visible = false };
		_screen = new TerminalScreenPanel(part) { Dock = DockType.Fill, ContextKey = ContextKeys.TerminalFocus, ShortcutsFirst = true };

		AddChild(_list);
		AddChild(_screen);
	}

	public void Focus() => _screen.RequestFocus();

	public void Sync() {
		_list.Visible = _part.Terminals.Count > 1;
		_list.Repaint();
		_screen.Repaint();
	}

	public override void PerformLayout() {
		_list.SetSize(ListWidth * Scale, Tall);
		base.PerformLayout();
	}
}

public sealed class TerminalScreenPanel : Panel
{
	private readonly PanelPart _part;

	public TerminalScreenPanel(PanelPart part) {
		_part = part;
		Name = "TerminalScreen";
		Cursor = CursorCode.IBeam;
		KeyboardInputEnabled = true;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		RequestFocus();
		return true;
	}

	public override bool OnKeyTyped(char character) {
		if (_part.Terminal is not { } terminal) return false;

		terminal.Write([character]);
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (_part.Terminal is not { } terminal) return false;

		bool control = (modifiers & KeyModifiers.Control) != 0;

		if (control && key is >= VirtualKey.A and <= VirtualKey.Z) {
			terminal.SendControl((char)key);
			return true;
		}

		if (key == VirtualKey.Right && !control && terminal.AcceptSuggestion()) return true;

		TerminalKey mapped = key switch {
			VirtualKey.Return => TerminalKey.Enter,
			VirtualKey.Back => TerminalKey.Backspace,
			VirtualKey.Up => TerminalKey.Up,
			VirtualKey.Down => TerminalKey.Down,
			VirtualKey.Left => TerminalKey.Left,
			VirtualKey.Right => TerminalKey.Right,
			VirtualKey.Home => TerminalKey.Home,
			VirtualKey.End => TerminalKey.End,
			VirtualKey.Delete => TerminalKey.Delete,
			VirtualKey.PageUp => TerminalKey.PageUp,
			VirtualKey.PageDown => TerminalKey.PageDown,
			_ => TerminalKey.None,
		};

		return terminal.SendKey(mapped, control);
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		_part.Terminal?.Screen.ScrollBy(delta / 120 * 3);
		Repaint();
		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;

		if (_part.Terminal is not { } session) {
			ITextFont ui = painter.GetUi();
			painter.DrawText(ui, "The terminal could not be started.", ScreenX + (Metrics.Space12 * scale), ScreenY + (Metrics.Space12 * scale) + ui.GetBaseline(), Theme.SectionHeaderForeground);
			return;
		}

		ITextFont font = painter.GetCode();
		float padding = Metrics.Space8 * scale;
		float cellWidth = font.GetCellWidth();
		float rowHeight = font.GetLineHeight();
		int columns = Math.Max(1, (int)((Wide - (padding * 2f)) / cellWidth));
		int rows = Math.Max(1, (int)((Tall - padding) / rowHeight));

		session.Resize(columns, rows);
		session.Drain();

		TerminalScreen screen = session.Screen;

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		lock (screen.Sync) {
			for (int row = 0; row < rows; row++) {
				ReadOnlySpan<TerminalCell> cells = screen.Row(row);

				if (!cells.IsEmpty) DrawRow(painter, cells, ScreenX + padding, ScreenY + (row * rowHeight), cellWidth, rowHeight, font);
			}

			if (screen.CursorVisible && screen.ScrollOffset == 0) {
				float caretX = ScreenX + padding + (screen.CursorColumn * cellWidth);
				float caretY = ScreenY + (screen.CursorRow * rowHeight);
				ReadOnlySpan<char> ghost = session.SuggestionRemainder;

				if (!ghost.IsEmpty) painter.DrawText(font, ghost[..Math.Min(ghost.Length, Math.Max(0, screen.Columns - screen.CursorColumn))], caretX, caretY + font.GetBaseline(), Theme.SectionHeaderForeground);

				painter.FillRectangle(caretX, caretY, cellWidth, rowHeight, Theme.CursorForeground.WithAlpha(0.6f));
			}
		}

		painter.PopClip();
	}

	private static void DrawRow(IPainter painter, ReadOnlySpan<TerminalCell> cells, float left, float top, float cellWidth, float rowHeight, ITextFont font) {
		Span<char> run = stackalloc char[512];
		int column = 0;

		while (column < cells.Length) {
			int start = column;
			int foreground = cells[start].Foreground;
			int background = cells[start].Background;
			int length = 0;

			while (column < cells.Length && length < run.Length && cells[column].Foreground == foreground && cells[column].Background == background) {
				run[length++] = cells[column].Character;
				column++;
			}

			float runLeft = left + (start * cellWidth);

			if (background != 0) painter.FillRectangle(runLeft, top, length * cellWidth, rowHeight, Convert(background));

			painter.DrawText(font, run[..length], runLeft, top + font.GetBaseline(), foreground == 0 ? Theme.EditorForeground : Convert(foreground));
		}
	}

	private static Color Convert(int argb) => Color.FromBytes((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF), (byte)((argb >> 24) & 0xFF));
}

public sealed class TerminalList : Panel
{
	private readonly PanelPart _part;
	private int _hover = -1;
	private bool _hoverClose;

	public TerminalList(PanelPart part) {
		_part = part;
		Name = "TerminalList";
	}

	private float RowHeight => Metrics.TreeRowHeight * Scale;

	private int RowAt(float y) {
		int row = (int)(y / RowHeight);
		return row >= 0 && row < _part.Terminals.Count ? row : -1;
	}

	private bool IsOverClose(float x) => x >= Wide - (Metrics.Space8 * Scale) - (Metrics.IconSize * Scale);

	public override void OnCursorMoved(float x, float y) {
		int row = RowAt(y);
		bool close = row >= 0 && IsOverClose(x);

		if (row == _hover && close == _hoverClose) return;

		_hover = row;
		_hoverClose = close;
		Repaint();
	}

	public override void OnCursorExited() {
		_hover = -1;
		_hoverClose = false;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		int row = RowAt(y);

		if (row < 0) return true;

		if (IsOverClose(x)) _part.CloseTerminal(row);
		else _part.ActivateTerminal(row);

		_part.FocusActive();

		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float rowHeight = RowHeight;
		ITextFont font = painter.GetUi();
		IReadOnlyList<TerminalSession> terminals = _part.Terminals;

		painter.DrawVerticalLine(ScreenX, ScreenY, ScreenY + Tall, Theme.PanelBorder);

		for (int i = 0; i < terminals.Count; i++) {
			float top = ScreenY + (i * rowHeight);

			if (top + rowHeight > ScreenY + Tall) break;

			bool active = i == _part.ActiveTerminal;
			bool hovered = i == _hover;
			float inset = Metrics.Space4 * scale;

			if (active || hovered) painter.FillRoundedRectangle(ScreenX + inset, top, Wide - (inset * 2f), rowHeight, Metrics.RadiusSmall * scale, active ? Theme.ListActiveSelectionBackground : Theme.ListHoverBackground);

			float baseline = top + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float centreY = top + (rowHeight / 2f);
			Color colour = active ? Theme.TabActiveForeground : Theme.ChromeForeground;
			float closeSize = Metrics.IconSize * scale;
			bool showClose = active || hovered;

			painter.DrawIcon(Codicon.Terminal, ScreenX + (Metrics.Space12 * scale), centreY, closeSize, colour);

			painter.PushClip(ScreenX, top, Wide - (Metrics.Space16 * scale) - (showClose ? closeSize : 0f), rowHeight);
			painter.DrawText(font, terminals[i].Title, ScreenX + ((Metrics.Space16 + Metrics.Space4) * scale), baseline, colour);
			painter.PopClip();

			if (showClose) painter.DrawIcon(Codicon.Close, ScreenX + Wide - (Metrics.Space8 * scale) - (closeSize / 2f), centreY, closeSize, _hoverClose && hovered ? Theme.TabActiveForeground : Theme.SectionHeaderForeground);
		}
	}
}
