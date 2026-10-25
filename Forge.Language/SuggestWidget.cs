using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Language;

public sealed class SuggestWidget : Panel
{
	public const float ListWidth = 430f;

	private const int MaxVisibleRows = 12;
	private const float RowPaddingRight = 10f;
	private const float IconMarginLeft = 2f;
	private const float IconMarginRight = 4f;
	private const float DetailsMarginEms = 1.1f;
	private const float DetailsMaxFraction = 0.7f;

	private readonly List<CompletionEntry> _all = [];
	private readonly List<int> _rows = [];
	private readonly List<(int Start, int Count)> _highlights = [];
	private readonly List<int> _matchPool = [];
	private readonly List<int> _scratchPool = [];
	private readonly List<(int Index, int Score, int Start, int Count)> _scored = [];

	private int _scrollRow;
	private int _hoverRow = -1;
	private int _detailFor = -1;

	public SuggestWidget() {
		Name = "Suggest";
		Visible = false;
		Details = new SuggestDetails(this);
	}

	public SuggestDetails Details { get; }

	public Action? Accepted { get; set; }

	public bool IsOpen { get; private set; }

	public int Selected { get; private set; }

	public bool DetailsVisible { get; set; }

	public int Line { get; private set; }

	public int RequestColumn { get; private set; }

	public int WordStartColumn { get; private set; }

	public string Detail { get; private set; } = string.Empty;

	public string Documentation { get; private set; } = string.Empty;

	public CompletionEntry? Current => Selected >= 0 && Selected < _rows.Count ? _all[_rows[Selected]] : null;

	private float RowHeight => Ui?.GetFonts().GetCode().GetLineHeight() ?? 1f;

	public int Generation { get; private set; }

	public void Show(IReadOnlyList<CompletionEntry> items, int line, int requestColumn, string leading, int caretColumn) {
		Generation++;
		_all.Clear();
		_all.AddRange(items);

		Line = line;
		RequestColumn = requestColumn;
		WordStartColumn = requestColumn;
		_detailFor = -1;
		Detail = string.Empty;
		Documentation = string.Empty;
		_hoverRow = -1;

		IsOpen = _all.Count > 0;
		Filter(leading, caretColumn);
	}

	public void Hide() {
		IsOpen = false;
		_all.Clear();
		_rows.Clear();
		_highlights.Clear();
		_matchPool.Clear();
		_detailFor = -1;
		_hoverRow = -1;
	}

	public void Filter(string leading, int caretColumn) {
		if (!IsOpen) return;

		_rows.Clear();
		_highlights.Clear();
		_matchPool.Clear();
		_scratchPool.Clear();
		_scored.Clear();

		Span<int> matched = stackalloc int[FuzzyScore.MaxMatches];
		int anchor = caretColumn;

		for (int i = 0; i < _all.Count; i++) {
			CompletionEntry entry = _all[i];
			int wordLength = Math.Clamp(caretColumn - entry.ReplaceStartColumn, 0, Math.Max(0, Math.Min(leading.Length, caretColumn - 1)));
			ReadOnlySpan<char> word = wordLength == 0 ? [] : leading.AsSpan(caretColumn - 1 - wordLength, wordLength);

			if (word.IsEmpty) {
				_scored.Add((i, -100, 0, 0));
				anchor = Math.Min(anchor, entry.ReplaceStartColumn);
				continue;
			}

			string label = entry.Label;
			string filter = entry.FilterText is { Length: > 0 } text ? text : label;

			if (!FuzzyScore.TryScore(word, filter, out int score, matched, out int count)) continue;

			if (!string.Equals(filter, label, StringComparison.OrdinalIgnoreCase) && !FuzzyScore.TryScore(word, label, out _, matched, out count)) count = 0;

			_scored.Add((i, score, _scratchPool.Count, count));
			foreach (int position in matched[..count]) _scratchPool.Add(position);
			anchor = Math.Min(anchor, entry.ReplaceStartColumn);
		}

		_scored.Sort(static (left, right) => right.Score != left.Score ? right.Score.CompareTo(left.Score) : left.Index.CompareTo(right.Index));

		foreach ((int index, _, int start, int count) in _scored) {
			_highlights.Add((_matchPool.Count, count));

			for (int m = 0; m < count; m++) _matchPool.Add(_scratchPool[start + m]);

			_rows.Add(index);
		}

		if (_rows.Count == 0) {
			Hide();
			return;
		}

		WordStartColumn = Math.Min(anchor, caretColumn);
		Selected = 0;
		_scrollRow = 0;
		_detailFor = -1;
	}

	public void MoveSelection(int delta) {
		if (!IsOpen || _rows.Count == 0) return;

		Selected = (((Selected + delta) % _rows.Count) + _rows.Count) % _rows.Count;
		ScrollToSelection();
	}

	public void MovePage(int direction) => MoveSelection(direction * MaxVisibleRows);

	private void ScrollToSelection() {
		if (Selected < _scrollRow) _scrollRow = Selected;
		else if (Selected >= _scrollRow + MaxVisibleRows) _scrollRow = Selected - MaxVisibleRows + 1;

		_scrollRow = Math.Clamp(_scrollRow, 0, Math.Max(0, _rows.Count - MaxVisibleRows));
	}

	public void SetDetail(int index, string detail, string documentation) {
		if (Current is not { } current || current.Index != index) return;

		_detailFor = index;
		Detail = detail;
		Documentation = documentation;
	}

	public int? PendingDetail => IsOpen && Current is { } current && current.Index != _detailFor ? current.Index : null;

	public void Select(int row) {
		if (row < 0 || row >= _rows.Count) return;

		Selected = row;
		ScrollToSelection();
	}

	public void Place(float caretX, float caretTop, float caretHeight, float areaTop, float areaBottom, float areaRight, float originX, float originY) {
		Visible = IsOpen && _rows.Count > 0;

		if (!Visible) {
			Details.Visible = false;
			return;
		}

		float scale = Scale;
		float width = ListWidth * scale;
		float height = Math.Min(MaxVisibleRows, _rows.Count) * RowHeight;
		float below = caretTop + caretHeight;
		float top = below + height <= areaBottom ? below : Math.Max(areaTop, caretTop - height);
		float left = Math.Max(0f, Math.Min(caretX, areaRight - width));

		SetBounds(left - originX, top - originY, width, height);
		Details.Place(left, top, width, areaTop, areaBottom, areaRight, originX, originY);
	}

	private int RowAt(float y) {
		int row = _scrollRow + (int)(y / RowHeight);
		return row >= 0 && row < _rows.Count ? row : -1;
	}

	public override void OnCursorMoved(float x, float y) {
		int row = RowAt(y);

		if (row == _hoverRow) return;

		_hoverRow = row;
		Repaint();
	}

	public override void OnCursorExited() {
		_hoverRow = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		int row = RowAt(y);

		if (row < 0) return true;

		Select(row);
		Accepted?.Invoke();
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		_scrollRow = Math.Clamp(_scrollRow - (delta / 120 * 3), 0, Math.Max(0, _rows.Count - MaxVisibleRows));
		Repaint();
		return true;
	}

	public override void Paint(IPainter painter) {
		if (_rows.Count == 0) return;

		float scale = Scale;
		ITextFont font = painter.GetCode();
		ITextFont bold = painter.GetCodeStrong();
		ITextFont small = painter.GetCodeSmall();
		float rowHeight = font.GetLineHeight();
		int visible = Math.Min(MaxVisibleRows, _rows.Count);
		float radius = Metrics.RadiusLarge * scale;
		float iconSize = Metrics.IconSize * scale;
		float labelLeft = ScreenX + (IconMarginLeft * 2f * scale) + iconSize + (IconMarginRight * scale);
		float rowRight = ScreenX + Wide - (RowPaddingRight * scale);

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
		painter.PushRoundedClip(ScreenX, ScreenY, Wide, Tall, radius);

		for (int row = _scrollRow; row < _rows.Count && row < _scrollRow + visible; row++) {
			CompletionEntry entry = _all[_rows[row]];
			float rowTop = ScreenY + ((row - _scrollRow) * rowHeight);
			bool selected = row == Selected;

			if (selected) painter.FillRectangle(ScreenX, rowTop, Wide, rowHeight, Theme.ListActiveSelectionBackground);
			else if (row == _hoverRow) painter.FillRectangle(ScreenX, rowTop, Wide, rowHeight, Theme.ListHoverBackground);

			float centreY = rowTop + (rowHeight / 2f);
			float baseline = rowTop + font.GetBaseline();
			Color foreground = selected ? Theme.SuggestSelectedForeground : Theme.SuggestForeground;

			painter.DrawIcon(IconFor(entry.Kind), ScreenX + (IconMarginLeft * scale) + (iconSize / 2f), centreY, iconSize, selected ? Theme.SuggestSelectedForeground : ColorFor(entry.Kind));

			bool hasLabelDetails = entry.LabelDetail.Length > 0 || entry.LabelDescription.Length > 0;
			string right = hasLabelDetails ? entry.LabelDescription : selected && !DetailsVisible ? entry.Detail : string.Empty;
			float detailsLeft = rowRight;

			if (right.Length > 0) {
				string text = right.ReplaceLineEndings(string.Empty);
				float detailWidth = Math.Min(small.Measure(text), (Wide - (labelLeft - ScreenX)) * DetailsMaxFraction);

				detailsLeft = rowRight - detailWidth;
				painter.PushClip(detailsLeft, rowTop, detailWidth, rowHeight);
				painter.DrawText(small, text, detailsLeft, rowTop + ((rowHeight - small.GetLineHeight()) / 2f) + small.GetBaseline(), foreground);
				painter.PopClip();
			}

			float labelLimit = Math.Max(0f, detailsLeft < rowRight ? detailsLeft - labelLeft - (DetailsMarginEms * font.GetSizeInPixels()) : rowRight - labelLeft);

			painter.PushClip(labelLeft, rowTop, labelLimit, rowHeight);
			painter.DrawText(font, entry.Label, labelLeft, baseline, foreground);

			(int matchStart, int matchCount) = _highlights[row];

			for (int m = matchStart; m < matchStart + matchCount; m++) {
				int position = _matchPool[m];

				if (position < entry.Label.Length) painter.DrawText(bold, entry.Label.AsSpan(position, 1), labelLeft + font.Measure(entry.Label.AsSpan(0, position)), baseline, Theme.SuggestHighlightForeground);
			}

			if (entry.LabelDetail is { Length: > 0 } signature) painter.DrawText(font, signature.ReplaceLineEndings(string.Empty), labelLeft + font.Measure(entry.Label), baseline, foreground.WithAlpha(foreground.A * 0.6f));

			painter.PopClip();
		}

		painter.PopRoundedClip();

		if (_rows.Count <= visible) return;

		float thumbHeight = Math.Max(20f * scale, Tall * visible / _rows.Count);
		float thumbTop = ScreenY + ((Tall - thumbHeight) * (_scrollRow / (float)Math.Max(1, _rows.Count - visible)));
		painter.FillRoundedRectangle(ScreenX + Wide - (6f * scale), thumbTop, 4f * scale, thumbHeight, 2f * scale, Theme.ScrollbarSlider);
	}

	private static Codicon IconFor(string kind) => kind switch {
		"Method" or "Function" or "Constructor" => Codicon.SymbolMethod,
		"Field" => Codicon.SymbolField,
		"Variable" => Codicon.SymbolVariable,
		"Class" => Codicon.SymbolClass,
		"Struct" => Codicon.SymbolStructure,
		"Interface" => Codicon.SymbolInterface,
		"Module" => Codicon.SymbolNamespace,
		"Property" => Codicon.SymbolProperty,
		"Event" => Codicon.SymbolEvent,
		"Operator" => Codicon.SymbolOperator,
		"Unit" or "Value" or "Enum" => Codicon.SymbolEnum,
		"EnumMember" => Codicon.SymbolEnumMember,
		"Constant" => Codicon.SymbolConstant,
		"Keyword" => Codicon.SymbolKeyword,
		"Snippet" => Codicon.SymbolSnippet,
		"File" => Codicon.SymbolFile,
		"Folder" => Codicon.Folder,
		"TypeParameter" => Codicon.SymbolParameter,
		"Reference" => Codicon.SymbolMisc,
		_ => Codicon.SymbolText,
	};

	private static Color ColorFor(string kind) => kind switch {
		"Method" or "Function" or "Constructor" => Color.Parse("#B392F0"),
		"Class" or "Event" or "Enum" => Color.Parse("#FF9800"),
		"Field" or "Interface" or "Variable" or "EnumMember" or "Property" => Color.Parse("#79B8FF"),
		_ => Theme.SuggestForeground,
	};
}
