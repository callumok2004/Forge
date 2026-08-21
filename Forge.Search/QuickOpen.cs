using Forge.Common.Render;
using Forge.Common.Workspace;
using Forge.Core;
using Forge.Theming;
using Forge.Workspace;

namespace Forge.Search;

public sealed class QuickOpen
{
	private const int MaxResults = 12;
	private const float RowHeight = 26f;
	private const float InputHeight = 40f;
	private const float PanelWidth = 620f;
	private const float PaddingX = 14f;

	private readonly FuzzyMatch[] _matches = new FuzzyMatch[MaxResults];
	private readonly System.Text.StringBuilder _query = new();

	private IFileIndex? _index;
	private int _matchCount;
	private int _selected;
	private int _hover = -1;

	private float _left;
	private float _top;
	private float _width;
	private float _height;
	private float _listTop;
	private float _rowHeight;

	public bool IsOpen { get; private set; }

	public void Attach(IFileIndex index) => _index = index;

	public void Show() {
		IsOpen = true;
		_query.Clear();
		_selected = 0;
		_hover = -1;
		Refresh();
	}

	public void Hide() {
		IsOpen = false;
		_hover = -1;
	}

	public PixelRect Bounds { get; private set; }

	public bool Contains(float x, float y) =>
		IsOpen && x >= _left && x < _left + _width && y >= _top && y < _top + _height;

	public int RowAt(float x, float y) {
		if (!Contains(x, y) || y < _listTop || _rowHeight <= 0f) return -1;

		int row = (int)((y - _listTop) / _rowHeight);

		return row >= 0 && row < _matchCount ? row : -1;
	}

	public bool SetHover(int row) {
		bool changed = _hover != row;
		_hover = row;
		return changed;
	}

	public bool Select(int row) {
		if (row < 0 || row >= _matchCount) return false;

		_selected = row;
		return true;
	}

	public void Append(char character) {
		_query.Append(character);
		_selected = 0;
		Refresh();
	}

	public void Backspace() {
		if (_query.Length > 0) {
			_query.Length--;
			_selected = 0;
			Refresh();
		}
	}

	public void MoveSelection(int delta) {
		if (_matchCount == 0) return;

		_selected = ((_selected + delta) % _matchCount + _matchCount) % _matchCount;
	}

	public string? Accept() {
		if (_index is null || _matchCount == 0) return null;

		string relative = _index.GetFiles()[_matches[_selected].Index];
		return Path.Combine(RootOf(_index), relative);
	}

	private static string RootOf(IFileIndex index) => index.GetRootPath();

	private void Refresh() {
		if (_index is null) {
			_matchCount = 0;
			return;
		}

		string[] files = _index.GetFiles();

		Span<char> pattern = stackalloc char[128];
		int length = Math.Min(_query.Length, pattern.Length);
		for (int i = 0; i < length; i++) pattern[i] = _query[i];

		_matchCount = FuzzyMatcher.Rank(pattern[..length], files, _matches);
		_selected = Math.Min(_selected, Math.Max(0, _matchCount - 1));
	}

	public void OnIndexUpdated() {
		if (IsOpen) Refresh();
	}

	public void Render(IPainter painter, float windowWidth, float windowHeight, float scale) {
		if (!IsOpen || _index is null) return;

		ITextFont font = painter.GetUi();

		float panelWidth = Math.Min(PanelWidth * scale, windowWidth - (80f * scale));
		float inputHeight = InputHeight * scale;
		float rowHeight = RowHeight * scale;
		bool empty = _matchCount == 0 && _query.Length > 0;
		float panelHeight = inputHeight + (_matchCount * rowHeight) + (empty ? rowHeight : 0f);

		float x = (windowWidth - panelWidth) / 2f;
		float y = 80f * scale;

		_left = x;
		_top = y;
		_width = panelWidth;
		_height = panelHeight;
		_listTop = y + inputHeight;
		_rowHeight = rowHeight;

		Bounds = PixelRect.Around(0f, 0f, windowWidth, windowHeight);

		painter.FillRectangle(0f, 0f, windowWidth, windowHeight, new Color(0f, 0f, 0f, 0.35f));

		float panelRadius = Metrics.RadiusLarge * scale;
		painter.PushRoundedClip(x, y, panelWidth, panelHeight, panelRadius);
		painter.FillRoundedRectangle(x, y, panelWidth, panelHeight, panelRadius, Theme.WidgetBackground);

		float baseline = y + ((inputHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float textX = x + (PaddingX * scale);

		if (_query.Length == 0) {
			painter.DrawText(painter.GetUi(), "Go to file…", textX, baseline, Theme.SectionHeaderForeground);
		}
		else {
			Span<char> buffer = stackalloc char[128];
			int length = Math.Min(_query.Length, buffer.Length);
			for (int i = 0; i < length; i++) buffer[i] = _query[i];

			painter.DrawText(painter.GetUi(), buffer[..length], textX, baseline, Theme.EditorForeground);
			painter.FillRectangle(textX + font.Measure(buffer[..length]), y + (10f * scale), 2f,
				inputHeight - (20f * scale), Theme.CursorForeground);
		}

		painter.DrawHorizontalLine(y + inputHeight, x, x + panelWidth, Theme.WidgetBorder);

		string[] files = _index.GetFiles();
		for (int i = 0; i < _matchCount; i++) {
			float rowTop = y + inputHeight + (i * rowHeight);

			if (i == _selected || i == _hover) {
				float rowInset = Metrics.Space4 * scale;
				painter.FillRoundedRectangle(x + rowInset, rowTop, panelWidth - (rowInset * 2f), rowHeight,
					Metrics.RadiusSmall * scale,
					i == _selected ? Theme.ListActiveSelectionBackground : Theme.ListHoverBackground);
			}

			int fileIndex = _matches[i].Index;
			if (fileIndex >= files.Length) continue;

			string relative = files[fileIndex];
			float rowBaseline = rowTop + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

			int nameStart = relative.LastIndexOfAny(['/', '\\']) + 1;

			painter.PushClip(x, rowTop, panelWidth, rowHeight);
			painter.DrawText(painter.GetUi(), relative.AsSpan(nameStart), textX, rowBaseline,
				i == _selected ? Theme.TabActiveForeground : Theme.EditorForeground);

			if (nameStart > 0) {
				float directoryX = textX + font.Measure(relative.AsSpan(nameStart)) + (12f * scale);
				painter.DrawText(painter.GetUi(), relative.AsSpan(0, nameStart - 1), directoryX, rowBaseline,
					Theme.SectionHeaderForeground);
			}

			painter.PopClip();
		}

		if (empty) {
			float emptyBaseline = y + inputHeight + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			painter.DrawText(painter.GetUi(), "No matching files", textX, emptyBaseline, Theme.SectionHeaderForeground);
		}

		painter.PopRoundedClip();
	}
}
