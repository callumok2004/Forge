using System.Text;

using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Core;
using Forge.Search;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Commands;

public sealed class CommandPalette
{
	private const float RowHeight = 22f;
	private const float InputHeight = 30f;
	private const float PaddingX = 10f;
	private const float PaddingY = 6f;
	private const float Width = 600f;
	private const int MaxRows = 12;

	private readonly StringBuilder _query = new();
	private readonly List<(ICommand Command, int Score)> _matches = [];

	private ICommandRegistry? _registry;
	private int _selected;
	private int _hover = -1;

	private int _scroll;
	private float _left;
	private float _top;
	private float _width;
	private float _height;
	private float _listTop;
	private float _rowHeight;

	public bool IsOpen { get; private set; }

	public void Show(ICommandRegistry registry) {
		_registry = registry;
		IsOpen = true;
		_query.Clear();
		_selected = 0;
		_scroll = 0;
		_hover = -1;
		Rebuild();
	}

	public void Hide() {
		IsOpen = false;
		_query.Clear();
		_matches.Clear();
		_scroll = 0;
		_hover = -1;
	}

	public void Append(char character) {
		_query.Append(character);
		_selected = 0;
		_scroll = 0;
		Rebuild();
	}

	public void Backspace() {
		if (_query.Length > 0) {
			_query.Length--;
			_selected = 0;
			_scroll = 0;
			Rebuild();
		}
	}

	public void MoveSelection(int delta) {
		if (_matches.Count == 0) return;

		_selected = ((_selected + delta) % _matches.Count + _matches.Count) % _matches.Count;
		ScrollToSelection();
	}

	public PixelRect Bounds => IsOpen
		? PixelRect.Around(_left - 2f, _top - 2f, _width + 4f, _height + 4f)
		: PixelRect.Empty;

	public bool Contains(float x, float y) =>
		IsOpen && x >= _left && x < _left + _width && y >= _top && y < _top + _height;

	public int RowAt(float x, float y) {
		if (!Contains(x, y) || y < _listTop || _rowHeight <= 0f) return -1;

		int row = _scroll + (int)((y - _listTop) / _rowHeight);

		return row >= 0 && row < _matches.Count && row < _scroll + MaxRows ? row : -1;
	}

	public bool SetHover(int row) {
		bool changed = _hover != row;
		_hover = row;
		return changed;
	}

	public bool Select(int row) {
		if (row < 0 || row >= _matches.Count) return false;

		_selected = row;
		return true;
	}

	public bool ScrollByWheel(int wheelDelta) {
		int before = _scroll;

		_scroll -= wheelDelta / 120 * 3;
		ClampScroll();

		return _scroll != before;
	}

	private void ClampScroll() =>
		_scroll = Math.Clamp(_scroll, 0, Math.Max(0, _matches.Count - MaxRows));

	private void ScrollToSelection() {
		if (_selected < _scroll) _scroll = _selected;
		else if (_selected >= _scroll + MaxRows) _scroll = _selected - MaxRows + 1;

		ClampScroll();
	}

	public ICommand? Accept() =>
		_selected >= 0 && _selected < _matches.Count ? _matches[_selected].Command : null;

	private void Rebuild() {
		_matches.Clear();

		if (_registry is not { } registry) return;

		string query = _query.ToString();

		foreach (ICommand command in registry.All()) {
			if (query.Length == 0) {
				_matches.Add((command, 0));
				continue;
			}

			if (FuzzyMatcher.TryMatch(query, command.GetLabel(), out int score)) _matches.Add((command, score));
		}

		if (query.Length > 0) _matches.Sort(static (left, right) => right.Score.CompareTo(left.Score));
	}

	public void Render(IPainter painter, float windowWidth, float windowHeight, float scale) {
		if (!IsOpen) return;

		ITextFont font = painter.GetUi();

		float width = Math.Min(Width * scale, windowWidth - (Metrics.Space16 * 2f * scale));
		float left = (windowWidth - width) / 2f;
		float top = Metrics.Space16 * scale;

		int rows = Math.Min(_matches.Count, MaxRows);
		float height = (InputHeight * scale) + (rows * RowHeight * scale) + (PaddingY * 2f * scale);
		float radius = Metrics.RadiusLarge * scale;

		_left = left;
		_top = top;
		_width = width;
		_height = height;

		painter.PushRoundedClip(left, top, width, height, radius);
		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.WidgetBackground);

		float inputHeight = InputHeight * scale;

		TextBox.Draw(painter, _query.ToString(), "Type a command name", left + (PaddingX * scale),
			top + (PaddingY * scale), width - (PaddingX * 2f * scale), inputHeight - (PaddingY * scale),
			scale, focused: true);

		float cursor = top + inputHeight + (PaddingY * scale);
		float rowHeight = RowHeight * scale;

		_listTop = cursor;
		_rowHeight = rowHeight;

		ClampScroll();
		int first = _scroll;

		for (int i = first; i < first + rows; i++) {
			ICommand command = _matches[i].Command;

			if (i == _selected) {
				float inset = Metrics.Space4 * scale;

				painter.FillRoundedRectangle(left + inset, cursor, width - (inset * 2f), rowHeight,
					Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);
			}
			else if (i == _hover) {
				float inset = Metrics.Space4 * scale;

				painter.FillRoundedRectangle(left + inset, cursor, width - (inset * 2f), rowHeight,
					Metrics.RadiusSmall * scale, Theme.ListHoverBackground);
			}

			float baseline = cursor + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float textLeft = left + (PaddingX * scale);

			if (command.GetCategory() is { Length: > 0 } category) {
				string prefix = $"{category}: ";

				painter.DrawText(font, prefix, textLeft, baseline, Theme.SectionHeaderForeground);
				textLeft += font.Measure(prefix);
			}

			painter.DrawText(font, command.GetTitle(), textLeft, baseline,
				i == _selected ? Theme.TabActiveForeground : Theme.EditorForeground);

			cursor += rowHeight;
		}

		painter.PopRoundedClip();

		painter.StrokeRoundedRectangle(left, top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}
}
