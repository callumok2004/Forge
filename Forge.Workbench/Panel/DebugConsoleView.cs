using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class DebugConsoleView : Panel
{
	private const float FontSize = 14f;
	private const float FilterHeight = 24f;
	private const float FilterFontSize = 12f;

	private static readonly string[] ConsoleFamilies = ["Consolas", "Courier New", "Cascadia Mono"];
	private const float LineHeightEm = 1.4f;
	private const float PaddingLeft = 12f;
	private const float IndentWidth = 8f;
	private const float TwistieWidth = 16f;
	private const float ChevronSlot = 30f;
	private const int MaxEntries = 10000;
	private const int MaxHistory = 50;

	private static readonly Color InfoForeground = Color.Parse("#5aa8ff");
	private static readonly Color WarningForeground = Color.Parse("#e3b341");
	private static readonly Color ErrorForeground = Color.Parse("#ff8a8a");
	private static readonly Color NameForeground = Color.Parse("#c586c0");
	private static readonly Color ValueForeground = Color.Parse("#cccccc99");
	private static readonly Color StringForeground = Color.Parse("#ce9178");
	private static readonly Color NumberForeground = Color.Parse("#b5cea8");
	private static readonly Color BooleanForeground = Color.Parse("#4e94ce");

	private readonly Lock _sync = new();
	private readonly List<Entry> _pending = [];
	private readonly List<Entry> _entries = [];
	private readonly List<Line> _lines = [];
	private readonly List<int> _entryLineStart = [];
	private readonly List<(int Line, int Start, int Length)> _rows = [];
	private readonly List<string> _history = [];
	private readonly ConsoleInput _input;

	private string _filter = string.Empty;
	private bool _linesDirty = true;
	private int _rebuildFrom;
	private float _wrappedWidth = -1f;
	private float _scrollY;
	private bool _stickToBottom = true;
	private (int Line, int Column) _anchor = (-1, 0);
	private (int Line, int Column) _focus = (-1, 0);
	private bool _selecting;
	private int _historyIndex = -1;
	private ITextFont? _font;
	private float _rowHeight = 20f;

	public DebugConsoleView() {
		Name = "DebugConsole";
		Dock = DockType.Fill;
		Cursor = CursorCode.IBeam;
		KeyboardInputEnabled = true;

		_input = new ConsoleInput(this) { Dock = DockType.Bottom };
		AddChild(_input);

		Filter = new TextEntry { Name = "DebugConsoleFilter", Placeholder = "Filter (e.g. text, !exclude, \\escape)", FontSize = FilterFontSize };
		Filter.TextChanged = () => SetFilter(Filter.Text);
	}

	public TextEntry Filter { get; }

	public static float FilterControlHeight => FilterHeight;

	public bool FilterInBody {
		get;
		set {
			if (field == value) return;

			field = value;
			InvalidateLayout();
			Repaint();
		}
	}

	private float FilterBand => FilterInBody ? FilterHeight * Scale : 0f;

	public Func<string, Task<DebugVariable?>>? Evaluate { get; set; }

	public Func<int, Task<IReadOnlyList<DebugVariable>>>? LoadChildren { get; set; }

	public Action<Action>? Post { get; set; }

	public void Append(string category, string text) {
		Color color = category switch {
			"stderr" => ErrorForeground,
			"console" => WarningForeground,
			_ => InfoForeground,
		};

		lock (_sync) _pending.Add(new Entry(EntryKind.Output, text, color));

		Post?.Invoke(Flush);
	}

	public void Clear() {
		lock (_sync) _pending.Clear();

		_entries.Clear();
		_anchor = _focus = (-1, 0);
		_scrollY = 0f;
		_stickToBottom = true;
		Invalidate(0);
		Repaint();
	}

	public void SetFilter(ReadOnlySpan<char> filter) {
		if (filter.SequenceEqual(_filter)) return;

		_filter = filter.ToString();
		Invalidate(0);
		Repaint();
	}

	public void FocusInput() => _input.Entry.RequestFocus();

	private void Flush() {
		lock (_sync) {
			if (_pending.Count == 0) return;

				int from = _entries.Count;

			foreach (Entry entry in _pending) {
				if (entry.Kind == EntryKind.Output && _entries.Count > 0 && _entries[^1] is { Kind: EntryKind.Output, Open: true } last && last.Color.Equals(entry.Color)) {
					last.Text += entry.Text;
					from = Math.Min(from, _entries.Count - 1);
				}
				else _entries.Add(entry);

				if (entry.Kind == EntryKind.Output) _entries[^1].Open = !_entries[^1].Text.EndsWith('\n');
			}

			_pending.Clear();
			Invalidate(from);
		}

		if (_entries.Count > MaxEntries) {
			_entries.RemoveRange(0, _entries.Count - MaxEntries);
			Invalidate(0);
		}

		Repaint();
	}

	private void Invalidate(int entry) {
		_rebuildFrom = _linesDirty ? Math.Min(_rebuildFrom, entry) : entry;
		_linesDirty = true;
	}

	private async void Submit(string expression) {
		if (expression.Length == 0) return;

		_history.Remove(expression);
		_history.Add(expression);

		if (_history.Count > MaxHistory) _history.RemoveAt(0);

		_historyIndex = -1;
		Invalidate(_entries.Count);
		_entries.Add(new Entry(EntryKind.Input, expression, Theme.EditorForeground));
		_stickToBottom = true;
		Repaint();

		if (Evaluate is not { } evaluate) return;

		DebugVariable? result = await evaluate(expression).ConfigureAwait(false);

		Post?.Invoke(() => {
			Invalidate(_entries.Count);
			_entries.Add(result is null
				? new Entry(EntryKind.Output, "Unable to evaluate expression: no active debug session or the expression is invalid.\n", ErrorForeground)
				: new Entry(EntryKind.Result, result.Value, ValueColor(result)) { Variable = result });

			_stickToBottom = true;
			Repaint();
		});
	}

	private void Recall(int direction) {
		if (_history.Count == 0) return;

		_historyIndex = _historyIndex < 0 ? (direction < 0 ? _history.Count - 1 : -1) : _historyIndex + direction;

		if (_historyIndex >= _history.Count) _historyIndex = -1;

		if (_historyIndex < -1) _historyIndex = 0;

		_input.Entry.SetText(_historyIndex < 0 ? string.Empty : _history[_historyIndex]);
	}

	private static Color ValueColor(DebugVariable variable) {
		ReadOnlySpan<char> value = variable.Value;

		if (variable.VariablesReference > 0) return Theme.EditorForeground;

		if (value.Length > 0 && value[0] == '"') return StringForeground;

		if (value.SequenceEqual("true") || value.SequenceEqual("false")) return BooleanForeground;

		if (value.Length > 0 && (char.IsAsciiDigit(value[0]) || (value[0] == '-' && value.Length > 1 && char.IsAsciiDigit(value[1])))) return NumberForeground;

		return ValueForeground;
	}

	private async void Toggle(Entry entry) {
		if (entry.Variable is not { VariablesReference: > 0 } variable) return;

		entry.Expanded = !entry.Expanded;
		Invalidate(entry.Root?.Index ?? 0);
		Repaint();

		if (!entry.Expanded || entry.Children is not null || LoadChildren is not { } load) return;

		IReadOnlyList<DebugVariable> children = await load(variable.VariablesReference).ConfigureAwait(false);

		Post?.Invoke(() => {
			entry.Children = [.. children.Select(child => new Entry(EntryKind.Result, child.Value, ValueColor(child)) { Variable = child, Depth = entry.Depth + 1, Named = true, Root = entry.Root ?? entry })];
			Invalidate((entry.Root ?? entry).Index);
			Repaint();
		});
	}

	private void BuildLines() {
		int from = Math.Clamp(_rebuildFrom, 0, _entries.Count);

		if (from > _entryLineStart.Count) from = _entryLineStart.Count;

		int keep = from < _entryLineStart.Count ? _entryLineStart[from] : _lines.Count;

		_lines.RemoveRange(keep, _lines.Count - keep);
		_entryLineStart.RemoveRange(from, _entryLineStart.Count - from);

		ReadOnlySpan<char> filter = _filter.AsSpan().Trim();
		bool exclude = filter.Length > 1 && filter[0] == '!';

		if (exclude) filter = filter[1..];
		else if (filter.Length > 1 && filter[0] == '\\') filter = filter[1..];

		for (int i = from; i < _entries.Count; i++) {
			Entry entry = _entries[i];
			entry.Index = i;
			_entryLineStart.Add(_lines.Count);
			AddLines(entry, filter, exclude);
		}

		TrimRows(keep);
		_linesDirty = false;
		_rebuildFrom = _entries.Count;
	}

	private void TrimRows(int firstLine) {
		int low = 0;
		int high = _rows.Count;

		while (low < high) {
			int middle = (low + high) >>> 1;

			if (_rows[middle].Line < firstLine) low = middle + 1;
			else high = middle;
		}

		_rows.RemoveRange(low, _rows.Count - low);
	}

	private void AddLines(Entry entry, ReadOnlySpan<char> filter, bool exclude) {
		switch (entry.Kind) {
			case EntryKind.Output: {
					ReadOnlySpan<char> text = entry.Text.AsSpan();
					int length = text.Length > 0 && text[^1] == '\n' ? text.Length - 1 : text.Length;
					int start = 0;

					while (start <= length) {
						int newline = text[start..length].IndexOf('\n');
						int end = newline < 0 ? length : start + newline;
						int trimmed = end > start && text[end - 1] == '\r' ? end - 1 : end;
						ReadOnlySpan<char> line = text[start..trimmed];

						if (filter.IsEmpty || line.Contains(filter, StringComparison.OrdinalIgnoreCase) != exclude) _lines.Add(new Line(entry, start, trimmed - start, entry.Color, 0, null, LineKind.Output));

						if (newline < 0) break;

						start = end + 1;
					}

					break;
				}

			case EntryKind.Input:
				_lines.Add(new Line(entry, 0, entry.Text.Length, Theme.EditorForeground, 0, null, LineKind.Input));
				break;

			case EntryKind.Result:
				string label = entry.Label ??= entry.Named && entry.Variable is { } variable ? $"{variable.Name}: {entry.Text}" : entry.Text;
				_lines.Add(new Line(entry, 0, label.Length, entry.Color, entry.Depth, entry.Variable is { VariablesReference: > 0 } ? entry : null, entry.Named ? LineKind.Child : LineKind.Result, entry.Named && entry.Variable is { } named ? named.Name.Length + 2 : 0));

				if (entry.Expanded && entry.Children is { } children) {
					foreach (Entry child in children) AddLines(child, default, false);
				}

				break;
		}
	}

	private float TextLeft(in Line line) => (PaddingLeft + (line.Depth * IndentWidth) + (line.Kind is LineKind.Result or LineKind.Child or LineKind.Input ? TwistieWidth : 0f)) * Scale;

	private void Wrap(float width) {
		if (width != _wrappedWidth) _rows.Clear();

		if (_font is null) return;

		float cell = _font.GetCellWidth();
		int firstLine = _rows.Count == 0 ? 0 : _rows[^1].Line + 1;

		for (int i = firstLine; i < _lines.Count; i++) {
			Line line = _lines[i];
			int columns = Math.Max(1, (int)((width - TextLeft(line) - (PaddingLeft * Scale)) / cell));
			int length = line.Length;

			if (length == 0) {
				_rows.Add((i, 0, 0));
				continue;
			}

			for (int start = 0; start < length; start += columns) _rows.Add((i, start, Math.Min(columns, length - start)));
		}

		_wrappedWidth = width;
	}

	private float ContentHeight => Tall - _input.Tall - FilterBand;

	private float MaxScroll => Math.Max(0f, (_rows.Count * _rowHeight) - ContentHeight);

	private bool HasSelection => _anchor.Line >= 0 && _anchor != _focus;

	private ((int Line, int Column) Start, (int Line, int Column) End) Ordered() =>
		_anchor.Line < _focus.Line || (_anchor.Line == _focus.Line && _anchor.Column <= _focus.Column) ? (_anchor, _focus) : (_focus, _anchor);

	private (int Line, int Column) PositionAt(float x, float y) {
		if (_rows.Count == 0 || _font is null) return (-1, 0);

		int row = Math.Clamp((int)MathF.Floor((y - FilterBand + _scrollY) / _rowHeight), 0, _rows.Count - 1);
		(int line, int start, int length) = _rows[row];
		float left = TextLeft(_lines[line]);
		int column = Math.Clamp((int)MathF.Round((x - left) / _font.GetCellWidth()), 0, length);
		return (line, start + column);
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		RequestFocus();

		if (button != MouseButton.Left) return true;

		int row = (int)MathF.Floor((y - FilterBand + _scrollY) / _rowHeight);

		if ((uint)row < (uint)_rows.Count && _rows[row].Start == 0 && _lines[_rows[row].Line].Expandable is { } entry && x < TextLeft(_lines[_rows[row].Line])) {
			Toggle(entry);
			return true;
		}

		(int Line, int Column) position = PositionAt(x, y);

		if ((Ui?.GetModifiers() & KeyModifiers.Shift) != 0 && _anchor.Line >= 0) _focus = position;
		else _anchor = _focus = position;

		_selecting = position.Line >= 0;

		if (_selecting) Ui?.SetMouseCapture(this);

		Repaint();
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		(int line, int column) = PositionAt(x, y);

		if (line < 0) return true;

		ReadOnlySpan<char> text = _lines[line].Text;
		int start = Math.Min(column, text.Length);
		int end = start;

		while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;

		while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;

		_anchor = (line, start);
		_focus = (line, end);
		Repaint();
		return true;
	}

	public override void OnCursorMoved(float x, float y) {
		if (!_selecting) return;

		if (y < FilterBand) ScrollBy(-_rowHeight);
		else if (y > FilterBand + ContentHeight) ScrollBy(_rowHeight);

		(int Line, int Column) position = PositionAt(x, Math.Clamp(y, FilterBand, FilterBand + ContentHeight - 1f));

		if (position.Line < 0 || position == _focus) return;

		_focus = position;
		Repaint();
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		_selecting = false;

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		return true;
	}

	public override void OnMouseCaptureLost() => _selecting = false;

	public override bool OnMouseWheeled(int delta, float x, float y) {
		ScrollBy(-delta / 120f * 3f * _rowHeight);
		return true;
	}

	private void ScrollBy(float amount) {
		float next = Math.Clamp(_scrollY + amount, 0f, MaxScroll);

		if (next == _scrollY) return;

		_scrollY = next;
		_stickToBottom = _scrollY >= MaxScroll - 1f;
		Repaint();
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if ((modifiers & KeyModifiers.Control) == 0) return false;

		switch (key) {
			case VirtualKey.C when HasSelection:
				Clipboard.SetText(SelectedText());
				return true;

			case VirtualKey.A:
				if (_lines.Count == 0) return true;

				_anchor = (0, 0);
				_focus = (_lines.Count - 1, _lines[^1].Length);
				Repaint();
				return true;

			case (uint)'L':
				Clear();
				return true;
		}

		return false;
	}

	private string SelectedText() {
		((int startLine, int startColumn), (int endLine, int endColumn)) = Ordered();
		System.Text.StringBuilder builder = new();

		for (int line = startLine; line <= endLine; line++) {
			ReadOnlySpan<char> text = _lines[line].Text;
			int from = line == startLine ? Math.Min(startColumn, text.Length) : 0;
			int to = line == endLine ? Math.Min(endColumn, text.Length) : text.Length;

			if (line > startLine) builder.Append(Environment.NewLine);

			if (to > from) builder.Append(text[from..to]);
		}

		return builder.ToString();
	}

	public override void PerformLayout() {
		_input.SetSize(Wide, _input.PreferredHeight);
		base.PerformLayout();

		if (FilterInBody && ReferenceEquals(Filter.Parent, this)) Filter.SetBounds(0f, 0f, Wide, FilterHeight * Scale);
	}

	public override void OnScaleChanged() => _wrappedWidth = -1f;

	public override void Paint(IPainter painter) {
		if (Ui is null) return;

		float scale = Scale;
		ITextFont font = Ui.GetFonts().GetFamily(ConsoleFamilies, FontSize);

		_font = font;
		_rowHeight = MathF.Round(FontSize * LineHeightEm * scale);

		if (_linesDirty) BuildLines();

		if (_wrappedWidth != Wide || _rows.Count == 0 || _rows[^1].Line != _lines.Count - 1) Wrap(Wide);

		if (_stickToBottom) _scrollY = MaxScroll;
		else _scrollY = Math.Min(_scrollY, MaxScroll);

		float height = ContentHeight;
		int first = Math.Max(0, (int)(_scrollY / _rowHeight));
		int last = Math.Min(_rows.Count - 1, (int)((_scrollY + height) / _rowHeight));
		bool selection = HasSelection;
		((int Line, int Column) start, (int Line, int Column) end) = selection ? Ordered() : ((-1, 0), (-1, 0));
		Color highlight = HasFocus ? Theme.SelectionBackground : Theme.SelectionBackground.WithAlpha(0.6f);
		float cell = font.GetCellWidth();
		float baselineOffset = ((_rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

		float bodyTop = ScreenY + FilterBand;

		painter.PushClip(ScreenX, bodyTop, Wide, height);

		for (int row = first; row <= last; row++) {
			(int lineIndex, int offset, int length) = _rows[row];
			Line line = _lines[lineIndex];
			float top = bodyTop + (row * _rowHeight) - _scrollY;
			float left = ScreenX + TextLeft(line);

			if (selection && lineIndex >= start.Line && lineIndex <= end.Line) {
				int from = Math.Max(lineIndex == start.Line ? start.Column : 0, offset) - offset;
				int to = Math.Min(lineIndex == end.Line ? end.Column : line.Length, offset + length) - offset;

				if (lineIndex != end.Line && offset + length == line.Length) to++;

				if (to > from) painter.FillRectangle(left + (from * cell), top, (to - from) * cell, _rowHeight, highlight);
			}

			if (offset == 0) {
				float glyph = left - ((TwistieWidth / 2f) * scale);

				if (line.Kind == LineKind.Input) painter.DrawIcon(Codicon.ChevronRight, glyph, top + (_rowHeight / 2f), Metrics.IconSize * scale, Theme.EditorForeground.WithAlpha(0.6f));
				else if (line.Expandable is { } expandable) painter.DrawIcon(expandable.Expanded ? Codicon.ChevronDown : Codicon.ChevronRight, glyph, top + (_rowHeight / 2f), Metrics.IconSize * scale, Theme.EditorForeground);
			}

			ReadOnlySpan<char> text = line.Text.Slice(offset, length);
			int nameEnd = Math.Clamp(line.NameLength - offset, 0, length);

			if (nameEnd > 0) painter.DrawText(font, text[..nameEnd], left, top + baselineOffset, NameForeground);

			painter.DrawText(font, text[nameEnd..], left + (nameEnd * cell), top + baselineOffset, line.Color);
		}

		painter.PopClip();
	}

	private enum EntryKind
	{
		Output,
		Input,
		Result,
	}

	private enum LineKind
	{
		Output,
		Input,
		Result,
		Child,
	}

	private sealed class Entry(EntryKind kind, string text, Color color)
	{
		public EntryKind Kind { get; } = kind;

		public string Text { get; set; } = text;

		public Color Color { get; } = color;

		public bool Open { get; set; }

		public DebugVariable? Variable { get; init; }

		public int Depth { get; init; }

		public bool Named { get; init; }

		public bool Expanded { get; set; }

		public List<Entry>? Children { get; set; }

		public string? Label { get; set; }

		public Entry? Root { get; init; }

		public int Index { get; set; }
	}

	private readonly record struct Line(Entry Source, int Start, int Length, Color Color, int Depth, Entry? Expandable, LineKind Kind, int NameLength = 0)
	{
		public ReadOnlySpan<char> Text => (Source.Label ?? Source.Text).AsSpan(Start, Length);
	}

	private sealed class ConsoleInput : Panel
	{
		private readonly DebugConsoleView _owner;

		public ConsoleInput(DebugConsoleView owner) {
			_owner = owner;
			Name = "DebugConsoleInput";
			Entry = new TextEntry { Name = "DebugConsoleEntry", Dock = DockType.Fill, FontSize = FontSize, Submitted = Submit };
			AddChild(Entry);
		}

		public TextEntry Entry { get; }

		private const float InputMargin = 4f;

		public float PreferredHeight => MathF.Round(FontSize * LineHeightEm * Scale) + (InputMargin * 2f * Scale) + (8f * Scale);

		private void Submit() {
			string expression = Entry.GetString();
			Entry.Clear();
			_owner.Submit(expression);
		}

		public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
			if (key == VirtualKey.Up) {
				_owner.Recall(-1);
				return true;
			}

			if (key != VirtualKey.Down) return false;

			_owner.Recall(1);
			return true;
		}

		public override void PerformLayout() {
			float scale = Scale;
			DockPadding = new Thickness(ChevronSlot * scale, (InputMargin * scale) + scale, InputMargin * 2f * scale, InputMargin * scale);
			base.PerformLayout();
		}

		public override void Paint(IPainter painter) {
			float scale = Scale;
			painter.FillRectangle(ScreenX + (InputMargin * 2f * scale), ScreenY, Wide - (InputMargin * 4f * scale), scale, Theme.InputBorder);
			painter.DrawIcon(Codicon.ChevronRight, ScreenX + ((8f + 8f) * scale), ScreenY + (Tall / 2f) + (scale / 2f), Metrics.IconSize * scale, Theme.EditorForeground);
		}
	}
}
