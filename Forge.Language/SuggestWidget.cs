using Forge.Commands;
using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Editor;
using Forge.Theming;

namespace Forge.Language;

public sealed class SuggestWidget
{
	private const float Width = 430f;

	private const int MaxVisibleRows = 12;

	private const float RowPaddingRight = 10f;

	private const float IconMarginLeft = 2f;

	private const float IconMarginRight = 4f;

	private const float DetailsMarginEms = 1.1f;

	private const float DetailsMaxFraction = 0.7f;

	private readonly List<CompletionEntry> _all = [];

	private readonly List<int> _rows = [];

	private readonly Dictionary<int, int[]> _highlights = [];

	private int _scrollRow;
	private int _hoverRow = -1;

	public bool IsOpen { get; private set; }

	public int Selected { get; private set; }

	public bool DetailsVisible { get; set; }

	private float _left;
	private float _top;
	private float _height;
	private float _rowHeight = 1f;
	private float _scale = 1f;

	public int Line { get; private set; }

	public int RequestColumn { get; private set; }

	public int WordStartColumn { get; private set; }

	private string _leading = string.Empty;
	private int _caretColumn;

	private string _detail = string.Empty;
	private string _documentation = string.Empty;

	private int _detailFor = -1;

	public CompletionEntry? Current =>
		Selected >= 0 && Selected < _rows.Count ? _all[_rows[Selected]] : null;

	public void Show(IReadOnlyList<CompletionEntry> items, int line, int requestColumn,
		string leading, int caretColumn) {
		_all.Clear();
		_all.AddRange(items);

		Line = line;
		RequestColumn = requestColumn;
		WordStartColumn = requestColumn;
		_detailFor = -1;
		_detail = string.Empty;
		_documentation = string.Empty;
		_hoverRow = -1;

		IsOpen = _all.Count > 0;
		Filter(leading, caretColumn);
	}

	public void Hide() {
		IsOpen = false;
		_all.Clear();
		_rows.Clear();
		_highlights.Clear();
		_detailFor = -1;
		_hoverRow = -1;
	}

	public void Filter(string leading, int caretColumn) {
		if (!IsOpen) return;

		_leading = leading;
		_caretColumn = caretColumn;

		_rows.Clear();
		_highlights.Clear();

		List<(int Index, int Score, int[] Matched)> scored = [];
		int anchor = caretColumn;

		for (int i = 0; i < _all.Count; i++) {
			CompletionEntry entry = _all[i];

			int wordLength = Math.Clamp(caretColumn - entry.ReplaceStartColumn, 0,
				Math.Max(0, Math.Min(leading.Length, caretColumn - 1)));

			string word = wordLength == 0
				? string.Empty
				: leading.Substring(caretColumn - 1 - wordLength, wordLength);

			if (word.Length == 0) {
				scored.Add((i, -100, []));
				anchor = Math.Min(anchor, entry.ReplaceStartColumn);
				continue;
			}

			string label = entry.Label;
			string filter = entry.FilterText is { Length: > 0 } text ? text : label;

			if (!FuzzyScore.TryScore(word, filter, out int score, out int[] matched)) continue;

			if (!string.Equals(filter, label, StringComparison.OrdinalIgnoreCase) &&
				!FuzzyScore.TryScore(word, label, out _, out matched)) matched = [];

			scored.Add((i, score, matched));
			anchor = Math.Min(anchor, entry.ReplaceStartColumn);
		}

		scored.Sort(static (left, right) => right.Score != left.Score
			? right.Score.CompareTo(left.Score)
			: left.Index.CompareTo(right.Index));

		foreach ((int index, _, int[] matched) in scored) {
			if (matched.Length > 0) _highlights[_rows.Count] = matched;

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

	public void ScrollByWheel(int wheelDelta) {
		if (IsOpen) {
			_scrollRow = Math.Clamp(_scrollRow - ((wheelDelta / 120) * 3), 0,
				Math.Max(0, _rows.Count - MaxVisibleRows));
		}
	}

	public void SetDetail(int index, string detail, string documentation) {
		if (Current is { } current && current.Index == index) {
			_detailFor = index;
			_detail = detail;
			_documentation = documentation;
		}
	}

	public int? PendingDetail =>
		IsOpen && Current is { } current && current.Index != _detailFor ? current.Index : null;

	public int RowAt(float x, float y) {
		if (!IsOpen || x < _left || x > _left + (Width * _scale) || y < _top || y > _top + _height) return -1;

		int row = _scrollRow + (int)((y - _top) / _rowHeight);
		return row >= 0 && row < _rows.Count ? row : -1;
	}

	public int HoverRow => _hoverRow;

	public void SetHover(int row) => _hoverRow = row;

	public void Select(int row) {
		if (row >= 0 && row < _rows.Count) {
			Selected = row;
			ScrollToSelection();
		}
	}

	public bool Contains(float x, float y) =>
		IsOpen && x >= _left && x <= _left + (Width * _scale) && y >= _top && y <= _top + _height;

	public void Render(IPainter painter, float caretX, float caretTop, float caretHeight,
		float editorTop, float editorBottom, float editorRight, float scale) {
		if (!IsOpen || _rows.Count == 0) return;

		_scale = scale;

		ITextFont font = painter.GetCode();
		ITextFont bold = painter.GetCodeStrong();
		ITextFont small = painter.GetCodeSmall();

		_rowHeight = font.GetLineHeight();

		float width = Width * scale;
		int visible = Math.Min(MaxVisibleRows, _rows.Count);

		_height = visible * _rowHeight;

		float below = caretTop + caretHeight;
		_top = below + _height <= editorBottom ? below : Math.Max(editorTop, caretTop - _height);
		_left = Math.Max(0f, Math.Min(caretX, editorRight - width));

		float radius = Metrics.RadiusLarge * scale;

		painter.FillRoundedRectangle(_left, _top, width, _height, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(_left, _top, width, _height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		painter.PushRoundedClip(_left, _top, width, _height, radius);

		float iconSize = Metrics.IconSize * scale;
		float labelLeft = _left + ((IconMarginLeft * 2f) * scale) + iconSize + (IconMarginRight * scale);
		float rowRight = _left + width - (RowPaddingRight * scale);

		for (int row = _scrollRow; row < _rows.Count && row < _scrollRow + visible; row++) {
			CompletionEntry entry = _all[_rows[row]];
			float rowTop = _top + ((row - _scrollRow) * _rowHeight);
			bool isSelected = row == Selected;

			if (isSelected) {
				painter.FillRectangle(_left, rowTop, width, _rowHeight,
					Theme.ListActiveSelectionBackground);
			}
			else if (row == _hoverRow) {
				painter.FillRectangle(_left, rowTop, width, _rowHeight, Theme.ListHoverBackground);
			}

			float centreY = rowTop + (_rowHeight / 2f);
			float baseline = rowTop + ((_rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

			Color foreground = isSelected ? Theme.SuggestSelectedForeground : Theme.SuggestForeground;

			painter.DrawIcon(IconFor(entry.Kind),
				_left + (IconMarginLeft * scale) + (iconSize / 2f), centreY, iconSize,
				isSelected ? Theme.SuggestSelectedForeground : ColorFor(entry.Kind));

			bool hasLabelDetails = entry.LabelDetail.Length > 0 || entry.LabelDescription.Length > 0;

			string right = hasLabelDetails
				? entry.LabelDescription
				: isSelected && !DetailsVisible ? entry.Detail : string.Empty;

			float detailsLeft = rowRight;

			if (right.Length > 0) {
				string text = right.ReplaceLineEndings(string.Empty);

				float detailWidth = Math.Min(small.Measure(text),
					(width - (labelLeft - _left)) * DetailsMaxFraction);

				detailsLeft = rowRight - detailWidth;

				float detailBaseline = rowTop + ((_rowHeight - small.GetLineHeight()) / 2f) + small.GetBaseline();

				painter.PushClip(detailsLeft, rowTop, detailWidth, _rowHeight);
				painter.DrawText(small, text, detailsLeft, detailBaseline, foreground);
				painter.PopClip();
			}

			float labelLimit = Math.Max(0f, detailsLeft < rowRight
				? detailsLeft - labelLeft - (DetailsMarginEms * font.GetSizeInPixels())
				: rowRight - labelLeft);

			painter.PushClip(labelLeft, rowTop, labelLimit, _rowHeight);
			painter.DrawText(font, entry.Label, labelLeft, baseline, foreground);

			if (_highlights.TryGetValue(row, out int[]? matched)) {
				foreach (int position in matched) {
					if (position >= entry.Label.Length) continue;

					painter.DrawText(bold, entry.Label.AsSpan(position, 1),
						labelLeft + font.Measure(entry.Label.AsSpan(0, position)), baseline,
						Theme.SuggestHighlightForeground);
				}
			}

			if (entry.LabelDetail is { Length: > 0 } signature) {
				painter.DrawText(font, signature.ReplaceLineEndings(string.Empty),
					labelLeft + font.Measure(entry.Label), baseline, Fade(foreground, 0.6f));
			}

			painter.PopClip();
		}

		painter.PopRoundedClip();

		if (_rows.Count > visible) {
			float thumbHeight = Math.Max(20f * scale, _height * visible / _rows.Count);
			float thumbTop = _top + ((_height - thumbHeight) *
				(_scrollRow / (float)Math.Max(1, _rows.Count - visible)));

			painter.FillRoundedRectangle(_left + width - (6f * scale), thumbTop, 4f * scale,
				thumbHeight, 2f * scale, Theme.ScrollbarSlider);
		}

		if (DetailsVisible) RenderDetails(painter, editorTop, editorBottom, editorRight, scale);
	}

	private void RenderDetails(IPainter painter, float editorTop, float editorBottom,
		float editorRight, float scale) {
		if (_documentation.Length == 0 && _detail.Length == 0) return;

		ITextFont font = painter.GetUi();
		ITextFont type = painter.GetCodeSmall();

		float width = Width * scale;
		float listWidth = Width * scale;

		float padX = 5f * scale;
		float padY = 4f * scale;

		float left = _left + listWidth;

		if (left + width > editorRight) left = Math.Max(0f, _left - width);

		List<string> lines = [];

		foreach (string paragraph in _documentation.Split('\n')) {
			Wrap(font, paragraph.TrimEnd('\r'), width - (padX * 2f), lines);
		}

		float height = padY * 2f;

		if (_detail.Length > 0) height += type.GetLineHeight() + (_documentation.Length > 0 ? 12f * scale : 0f);

		int shown = Math.Min(lines.Count,
			Math.Max(1, (int)((editorBottom - _top - height) / font.GetLineHeight())));

		height += shown * font.GetLineHeight();

		float top = Math.Max(editorTop, Math.Min(_top, editorBottom - height));
		float radius = Metrics.RadiusLarge * scale;

		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(left, top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		painter.PushRoundedClip(left, top, width, height, radius);

		float y = top + padY;

		if (_detail.Length > 0) {
			painter.DrawText(type, _detail.ReplaceLineEndings(string.Empty), left + padX,
				y + type.GetBaseline(), Fade(Theme.SuggestForeground, 0.7f));

			y += type.GetLineHeight() + (_documentation.Length > 0 ? 12f * scale : 0f);
		}

		for (int i = 0; i < shown; i++) {
			painter.DrawText(font, lines[i], left + padX, y + font.GetBaseline(), Theme.SuggestForeground);
			y += font.GetLineHeight();
		}

		painter.PopRoundedClip();
	}

	private static Color Fade(Color color, float opacity) =>
		new(color.R, color.G, color.B, color.A * opacity);

	private static void Wrap(ITextFont font, string text, float width, List<string> into) {
		if (text.Length == 0 || font.Measure(text) <= width) {
			into.Add(text);
			return;
		}

		int start = 0;
		int lastSpace = -1;

		for (int i = 0; i < text.Length; i++) {
			if (text[i] == ' ') lastSpace = i;

			if (font.Measure(text.AsSpan(start, i - start + 1)) <= width) continue;

			int end = lastSpace > start ? lastSpace : i;

			into.Add(text[start..end]);
			start = lastSpace > start ? end + 1 : end;
			lastSpace = -1;
		}

		if (start < text.Length) into.Add(text[start..]);
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
