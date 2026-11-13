using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Terminal;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class TerminalView : Panel
{
	private const float ListWidth = 150f;

	private readonly PanelPart _part;
	private readonly TerminalList _list;
	private readonly List<TerminalScreenPanel> _screens = [];
	private readonly List<TerminalSession> _group = [];
	private readonly List<Splitter> _sashes = [];
	private readonly Dictionary<TerminalSession, float> _weights = [];

	private const float MinimumScreenWidth = 80f;

	public TerminalView(PanelPart part) {
		_part = part;
		Name = "Terminal";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_list = new TerminalList(part) { Dock = DockType.Right, Visible = false };
		AddChild(_list);
		Sync();
	}

	public void Focus() {
		foreach (TerminalScreenPanel screen in _screens) {
			if (screen.Visible && ReferenceEquals(screen.Session, _part.Terminal)) {
				screen.RequestFocus();
				return;
			}
		}
	}

	public void Sync() {
		_part.CopyActiveGroup(_group);

		while (_screens.Count < Math.Max(1, _group.Count)) {
			TerminalScreenPanel screen = new(_part) { Dock = DockType.None, ContextKey = ContextKeys.TerminalFocus, ShortcutsFirst = true };
			_screens.Add(screen);
			AddChild(screen);
		}

		for (int i = 0; i < _screens.Count; i++) {
			_screens[i].Session = i < _group.Count ? _group[i] : null;
			_screens[i].Visible = i < Math.Max(1, _group.Count);
			_screens[i].Repaint();
		}

		while (_sashes.Count < _group.Count - 1) {
			int boundary = _sashes.Count;
			Splitter sash = new(vertical: true) { Name = "TerminalSash" };
			sash.Dragged = (x, _) => Resize(boundary, x);
			_sashes.Add(sash);
			AddChild(sash);
		}

		for (int i = 0; i < _sashes.Count; i++) _sashes[i].Visible = i < _group.Count - 1;

		foreach (TerminalSession session in _weights.Keys) {
			if (!_part.Terminals.Contains(session)) _weights.Remove(session);
		}

		_list.Visible = _part.Terminals.Count > 1;
		_list.Repaint();
		InvalidateLayout();
	}

	public override void PerformLayout() {
		float scale = Scale;
		_list.SetSize(ListWidth * scale, Tall);
		base.PerformLayout();

		float width = _list.Visible ? Wide - _list.Wide : Wide;
		int count = Math.Max(1, _group.Count);
		float left = 0f;
		float total = 0f;
		float sashWidth = Metrics.SplitterHitWidth * scale;

		for (int i = 0; i < count; i++) total += Weight(i);

		for (int i = 0; i < count; i++) {
			float right = i == count - 1 ? width : MathF.Round(left + (width * Weight(i) / total));
			_screens[i].SetBounds(left, 0f, Math.Max(0f, right - left - (i < count - 1 ? scale : 0f)), Tall);

			if (i < count - 1) _sashes[i].SetBounds(right - (sashWidth / 2f), 0f, sashWidth, Tall);

			left = right;
		}
	}

	private float Weight(int index) => index < _group.Count && _weights.TryGetValue(_group[index], out float weight) ? weight : 1f;

	private void Resize(int boundary, float screenX) {
		if (boundary + 1 >= _group.Count) return;

		TerminalScreenPanel leftScreen = _screens[boundary];
		TerminalScreenPanel rightScreen = _screens[boundary + 1];
		float start = leftScreen.GetAbsPos().X;
		float pair = rightScreen.GetAbsPos().X + rightScreen.Wide - start;
		float minimum = Math.Min(MinimumScreenWidth * Scale, pair / 2f);
		float leftWidth = Math.Clamp(screenX - start, minimum, pair - minimum);
		float weights = Weight(boundary) + Weight(boundary + 1);

		_weights[_group[boundary]] = weights * leftWidth / pair;
		_weights[_group[boundary + 1]] = weights * (pair - leftWidth) / pair;
		InvalidateLayout();
		Repaint();
	}

	public override void PaintBackground(IPainter painter) {
		float scale = Scale;
		int count = Math.Max(1, _group.Count);

		for (int i = 0; i < count - 1; i++) painter.FillRectangle(ScreenX + _screens[i].X + _screens[i].Wide, ScreenY, scale, Tall, Theme.PanelBorder);
	}
}

public sealed partial class TerminalScreenPanel : Panel
{
	private readonly PanelPart _part;
	private TerminalLink? _link;
	private float _mouseX;
	private float _mouseY;

	private readonly record struct TerminalLink((int Line, int Column) Start, (int Line, int Column) End, string Target, int TargetLine, int TargetColumn, bool Url);

	[System.Text.RegularExpressions.GeneratedRegex(@"\b(?:https?|file)://[^\s""'<>()\[\]{}]+")]
	private static partial System.Text.RegularExpressions.Regex UrlPattern();

	[System.Text.RegularExpressions.GeneratedRegex(@"(?<path>(?:[A-Za-z]:[\\/]|\.{1,2}[\\/]|[\\/~])?[\w.\-\\/]*[\w\-]\.[A-Za-z0-9]+)(?:\((?<line>\d+)(?:,(?<column>\d+))?\)|:(?<line>\d+)(?::(?<column>\d+))?)?")]
	private static partial System.Text.RegularExpressions.Regex PathPattern();

	public TerminalSession? Session { get; set; }

	public TerminalScreenPanel(PanelPart part) {
		_part = part;
		Name = "TerminalScreen";
		Cursor = CursorCode.IBeam;
		KeyboardInputEnabled = true;
	}

	private (int Line, int Column) _anchor = (-1, 0);
	private (int Line, int Column) _focus = (-1, 0);
	private bool _selecting;
	private float _cellWidth = 1f;
	private float _rowHeight = 1f;

	private bool HasSelection => _anchor.Line >= 0 && _anchor != _focus;

	private (int Line, int Column) PositionAt(TerminalScreen screen, float x, float y) {
		float padding = Metrics.Space8 * Scale;
		int row = Math.Clamp((int)MathF.Floor(y / _rowHeight), 0, Math.Max(0, screen.Rows - 1));
		int column = Math.Clamp((int)MathF.Round((x - padding) / _cellWidth), 0, screen.Columns);
		return (screen.LineIndex(row), column);
	}

	private ((int Line, int Column) Start, (int Line, int Column) End) Ordered() =>
		_anchor.Line < _focus.Line || (_anchor.Line == _focus.Line && _anchor.Column <= _focus.Column) ? (_anchor, _focus) : (_focus, _anchor);

	public void ClearSelection() {
		if (_anchor.Line < 0) return;

		_anchor = _focus = (-1, 0);
		Repaint();
	}

	private string SelectedText(TerminalScreen screen) {
		((int startLine, int startColumn), (int endLine, int endColumn)) = Ordered();
		System.Text.StringBuilder builder = new();

		lock (screen.Sync) {
			for (int line = startLine; line <= endLine; line++) {
				ReadOnlySpan<TerminalCell> cells = screen.Line(line);
				int from = line == startLine ? Math.Min(startColumn, cells.Length) : 0;
				int to = line == endLine ? Math.Min(endColumn, cells.Length) : cells.Length;
				int length = builder.Length;

				for (int i = from; i < to; i++) builder.Append(cells[i].Character == '\0' ? ' ' : cells[i].Character);

				if (line == endLine) break;

				while (builder.Length > length && builder[^1] == ' ') builder.Length--;

				builder.Append(Environment.NewLine);
			}
		}

		while (builder.Length > 0 && char.IsWhiteSpace(builder[^1])) builder.Length--;

		return builder.ToString();
	}

	private bool CopySelection() {
		if (!HasSelection || Session is not { } session) return false;

		Clipboard.SetText(SelectedText(session.Screen));
		ClearSelection();
		return true;
	}

	private void Paste() {
		if (Session is not { } session) return;

		string text = Clipboard.GetText();

		if (text.Length == 0) return;

		session.Write(text.ReplaceLineEndings("\r"));
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (Session is { } pressed) _part.ActivateSession(pressed);

		RequestFocus();

		if (Session is not { } session) return true;

		if (button == MouseButton.Right) {
			if (!CopySelection()) Paste();

			return true;
		}

		if (button != MouseButton.Left) return true;

		if ((Ui?.GetModifiers() & KeyModifiers.Control) != 0 && LinkAt(session.Screen, x, y) is { } link) {
			OpenLink(link);
			return true;
		}

		(int Line, int Column) position = PositionAt(session.Screen, x, y);

		if ((Ui?.GetModifiers() & KeyModifiers.Shift) != 0 && _anchor.Line >= 0) _focus = position;
		else _anchor = _focus = position;

		_selecting = true;
		Ui?.SetMouseCapture(this);
		Repaint();
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || Session is not { } session) return false;

		TerminalScreen screen = session.Screen;
		(int line, int column) = PositionAt(screen, x, y);

		lock (screen.Sync) {
			string text = LogicalText(screen, line, out int[] starts, out int first);
			int offset = Math.Min(starts[line - first] + column, text.Length);
			(int start, int end) = WordRange.At(text, offset, WordRange.TerminalSeparators);

			_anchor = Locate(starts, first, start);
			_focus = Locate(starts, first, end);
		}

		Repaint();
		return true;
	}

	public override bool OnMouseTriplePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || Session is not { } session) return false;

		TerminalScreen screen = session.Screen;
		(int line, _) = PositionAt(screen, x, y);

		lock (screen.Sync) {
			(int first, int last) = screen.LogicalLine(line);
			_anchor = (first, 0);
			_focus = (last, screen.Line(last).Length);
		}

		Repaint();
		return true;
	}

	private static string LogicalText(TerminalScreen screen, int line, out int[] starts, out int first) {
		(first, int last) = screen.LogicalLine(line);
		starts = new int[last - first + 2];
		System.Text.StringBuilder builder = new();

		for (int i = first; i <= last; i++) {
			starts[i - first] = builder.Length;

			foreach (TerminalCell cell in screen.Line(i)) builder.Append(cell.Character == ' ' ? ' ' : cell.Character);
		}

		starts[^1] = builder.Length;
		return builder.ToString();
	}

	private static (int Line, int Column) Locate(int[] starts, int first, int offset) {
		int index = 0;

		while (index + 2 < starts.Length && starts[index + 1] < offset) index++;

		return (first + index, offset - starts[index]);
	}


	private TerminalLink? LinkAt(TerminalScreen screen, float x, float y) {
		(int line, int column) = PositionAt(screen, x - (_cellWidth / 2f), y);
		string text;
		int[] starts;
		int first;

		lock (screen.Sync) {
			if (screen.Line(line).IsEmpty) return null;

			text = LogicalText(screen, line, out starts, out first);
		}

		int offset = starts[line - first] + column;

		foreach (System.Text.RegularExpressions.Match match in UrlPattern().Matches(text)) {
			if (offset >= match.Index && offset < match.Index + match.Length) return new TerminalLink(Locate(starts, first, match.Index), Locate(starts, first, match.Index + match.Length), match.Value.TrimEnd('.', ',', ';', ':'), 0, 0, true);
		}

		foreach (System.Text.RegularExpressions.Match match in PathPattern().Matches(text)) {
			if (offset < match.Index || offset >= match.Index + match.Length) continue;

			string path = match.Groups["path"].Value;

			if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];

			string full;

			try {
				full = Path.GetFullPath(Path.Combine(_part.WorkingDirectory, path));
			}
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
				return null;
			}

			if (!File.Exists(full)) return null;

			int targetLine = int.TryParse(match.Groups["line"].ValueSpan, out int parsedLine) ? parsedLine : 1;
			int targetColumn = int.TryParse(match.Groups["column"].ValueSpan, out int parsedColumn) ? parsedColumn : 1;
			return new TerminalLink(Locate(starts, first, match.Index), Locate(starts, first, match.Index + match.Length), full, targetLine, targetColumn, false);
		}

		return null;
	}

	private void UpdateLink() {
		TerminalLink? link = !_selecting && Session is { } session && (Ui?.GetModifiers() & KeyModifiers.Control) != 0 ? LinkAt(session.Screen, _mouseX, _mouseY) : null;

		if (link == _link) return;

		_link = link;
		Cursor = link is null ? CursorCode.IBeam : CursorCode.Hand;
		Repaint();
	}

	private void OpenLink(TerminalLink link) {
		if (!link.Url) {
			_part.Navigate?.Invoke(link.Target, link.TargetLine, link.TargetColumn);
			return;
		}

		try {
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.Target) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) {
		}
	}

	public override void OnCursorExited() {
		if (_link is null) return;

		_link = null;
		Cursor = CursorCode.IBeam;
		Repaint();
	}

	public override void OnCursorMoved(float x, float y) {
		_mouseX = x;
		_mouseY = y;

		if (!_selecting) {
			UpdateLink();
			return;
		}

		if (Session is not { } session) return;

		if (y < 0f) session.Screen.ScrollBy(1);
		else if (y > Tall) session.Screen.ScrollBy(-1);

		(int Line, int Column) position = PositionAt(session.Screen, x, Math.Clamp(y, 0f, Tall - 1f));

		if (position == _focus) return;

		_focus = position;
		Repaint();
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_selecting) return false;

		_selecting = false;
		Ui?.SetMouseCapture(null);
		return true;
	}

	public override void OnMouseCaptureLost() => _selecting = false;

	public override bool OnKeyTyped(char character) {
		if (Session is not { } terminal) return false;

		ClearSelection();
		terminal.Screen.ScrollToBottom();
		terminal.Write([character]);
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (Session is not { } terminal) return false;

		bool control = (modifiers & KeyModifiers.Control) != 0;

		if (key == VirtualKey.Control) {
			UpdateLink();
			return false;
		}

		if (control && key == VirtualKey.C && CopySelection()) return true;

		if (control && key == VirtualKey.V) {
			Paste();
			return true;
		}

		if (control && key is >= VirtualKey.A and <= VirtualKey.Z) {
			ClearSelection();
			terminal.Screen.ScrollToBottom();
			terminal.SendControl((char)key);
			return true;
		}

		if (key == VirtualKey.Right && !control && terminal.AcceptSuggestion()) {
			ClearSelection();
			return true;
		}

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

		if (mapped == TerminalKey.None) return false;

		ClearSelection();
		terminal.Screen.ScrollToBottom();
		return terminal.SendKey(mapped, control);
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		Session?.Screen.ScrollBy(delta / 120 * 3);
		Repaint();
		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;

		if (Session is not { } session) {
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
		bool selection = HasSelection;
		((int Line, int Column) start, (int Line, int Column) end) = selection ? Ordered() : ((-1, 0), (-1, 0));
		Color highlight = HasFocus ? Theme.SelectionBackground : Theme.SelectionBackground.WithAlpha(0.6f);

		_cellWidth = cellWidth;
		_rowHeight = rowHeight;

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		lock (screen.Sync) {
			for (int row = 0; row < rows; row++) {
				ReadOnlySpan<TerminalCell> cells = screen.Row(row);
				float top = ScreenY + (row * rowHeight);
				int line = screen.LineIndex(row);

				if (selection && line >= start.Line && line <= end.Line) {
					int from = line == start.Line ? start.Column : 0;
					int to = line == end.Line ? end.Column : screen.Columns;

					if (to > from) painter.FillRectangle(ScreenX + padding + (from * cellWidth), top, (to - from) * cellWidth, rowHeight, highlight);
				}

				if (!cells.IsEmpty) DrawRow(painter, cells, ScreenX + padding, top, cellWidth, rowHeight, font);

				if (_link is { } link && line >= link.Start.Line && line <= link.End.Line) {
					int from = line == link.Start.Line ? link.Start.Column : 0;
					int to = line == link.End.Line ? link.End.Column : cells.Length;
					painter.FillRectangle(ScreenX + padding + (from * cellWidth), top + rowHeight - scale, (to - from) * cellWidth, scale, Theme.EditorForeground);
				}
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

			float indent = _part.IsSplitMember(i) ? Metrics.Space12 * scale : 0f;

			painter.DrawIcon(Codicon.Terminal, ScreenX + indent + (Metrics.Space12 * scale), centreY, closeSize, colour);

			painter.PushClip(ScreenX, top, Wide - (Metrics.Space16 * scale) - (showClose ? closeSize : 0f), rowHeight);
			painter.DrawText(font, terminals[i].Title, ScreenX + indent + ((Metrics.Space16 + Metrics.Space4) * scale), baseline, colour);
			painter.PopClip();

			if (showClose) painter.DrawIcon(Codicon.Close, ScreenX + Wide - (Metrics.Space8 * scale) - (closeSize / 2f), centreY, closeSize, _hoverClose && hovered ? Theme.TabActiveForeground : Theme.SectionHeaderForeground);
		}
	}
}
