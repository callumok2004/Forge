using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.UI.Controls;

public delegate void ListRowPainter(IPainter painter, int index, float left, float top, float width, float height, bool selected);

public sealed class ListPanel : Panel
{
	private int _count;
	private int _selected;
	private int _hover = -1;
	private int _first;
	private int _pressed = -1;

	public float RowHeight { get; init; } = 22f;

	public ListRowPainter? RowPainter { get; set; }

	public Action<int>? Activated { get; set; }

	public Action<int>? Hovered { get; set; }

	public int Selected => _selected;

	public int Count => _count;

	public int VisibleRows => Math.Max(1, (int)(Tall / (RowHeight * Scale)));

	public void SetCount(int count) {
		_count = count;
		_selected = Math.Clamp(_selected, 0, Math.Max(0, count - 1));
		_first = Math.Clamp(_first, 0, Math.Max(0, count - VisibleRows));
		_hover = -1;
		Repaint();
	}

	public void Select(int index) {
		if (_count == 0) return;

		_selected = Math.Clamp(index, 0, _count - 1);

		if (_selected < _first) _first = _selected;
		else if (_selected >= _first + VisibleRows) _first = _selected - VisibleRows + 1;

		Repaint();
	}

	public bool Navigate(uint key) {
		switch (key) {
			case VirtualKey.Up:
				Select(_count == 0 ? 0 : (_selected - 1 + _count) % _count);
				return true;

			case VirtualKey.Down:
				Select(_count == 0 ? 0 : (_selected + 1) % _count);
				return true;

			case VirtualKey.PageUp:
				Select(_selected - VisibleRows);
				return true;

			case VirtualKey.PageDown:
				Select(_selected + VisibleRows);
				return true;

			case VirtualKey.Return when _count > 0:
				Activated?.Invoke(_selected);
				return true;
		}

		return false;
	}

	private int RowAt(float y) {
		int row = _first + (int)(y / (RowHeight * Scale));
		return row >= 0 && row < _count ? row : -1;
	}

	public override void OnCursorMoved(float x, float y) {
		int row = RowAt(y);

		if (row == _hover) return;

		RepaintRow(_hover);
		_hover = row;
		RepaintRow(_hover);
		Hovered?.Invoke(row);
	}

	public override void OnCursorExited() {
		RepaintRow(_hover);
		_hover = -1;
		Hovered?.Invoke(-1);
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		_pressed = RowAt(y);

		if (_pressed >= 0) Select(_pressed);

		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		int row = RowAt(y);

		if (row >= 0 && row == _pressed) Activated?.Invoke(row);

		_pressed = -1;
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		int previous = _first;
		_first = Math.Clamp(_first - (delta / 120 * 3), 0, Math.Max(0, _count - VisibleRows));

		if (_first != previous) Repaint();

		return true;
	}

	private void RepaintRow(int row) {
		if (row < _first || row >= _first + VisibleRows) return;

		float height = RowHeight * Scale;
		Repaint(0f, (row - _first) * height, Wide, height);
	}

	public override void Paint(IPainter painter) {
		if (RowPainter is not { } paintRow) return;

		float scale = Scale;
		float height = RowHeight * scale;
		float inset = Metrics.Space4 * scale;
		int last = Math.Min(_count, _first + VisibleRows);

		for (int row = _first; row < last; row++) {
			float top = ScreenY + ((row - _first) * height);

			if (!painter.NeedsPaint(ScreenX, top, Wide, height)) continue;

			bool selected = row == _selected;

			if (selected) painter.FillRoundedRectangle(ScreenX + inset, top, Wide - (inset * 2f), height, Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);
			else if (row == _hover) painter.FillRoundedRectangle(ScreenX + inset, top, Wide - (inset * 2f), height, Metrics.RadiusSmall * scale, Theme.ListHoverBackground);

			paintRow(painter, row, ScreenX, top, Wide, height, selected);
		}
	}
}
