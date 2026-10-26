using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public readonly record struct Cell(float X, float Y, float Width, float Height, double Scale, bool Selected);

public delegate void CellPainter<T>(IPainter painter, T row, in Cell cell);

public sealed class TableColumn<T>(string key, string label, float width, Comparison<T> compare, CellPainter<T> paint)
{
	public string Key { get; } = key;

	public string Label { get; } = label;

	public float Width { get; set; } = width;

	public Comparison<T> Compare { get; } = compare;

	public CellPainter<T> Paint { get; } = paint;

	public bool Numeric { get; init; }

	public bool DefaultDescending { get; init; } = true;

	public float DefaultWidth { get; } = width;
}

public sealed class DataTable<T> : Panel where T : class
{
	private const float HeaderHeight = 24f;
	private const float RowHeight = 21f;
	private const float GripWidth = 6f;
	private const float ScrollbarWidth = 10f;
	private const float CellPadding = 8f;

	private readonly TableColumn<T>[] _columns;
	private readonly List<T> _visible = [];

	private IReadOnlyList<T> _rows = [];
	private Func<T, bool>? _filter;
	private int _sortColumn;
	private bool _descending;
	private float _scroll;
	private int _hover = -1;
	private T? _selected;
	private int _resizing = -1;
	private float _resizeStartX;
	private float _resizeStartWidth;
	private bool _draggingThumb;
	private float _thumbGrab;
	private double _scale = 1;

	public DataTable(TableColumn<T>[] columns, string initialSort) {
		_columns = columns;
		_sortColumn = Math.Max(0, Array.FindIndex(columns, column => column.Key == initialSort));
		_descending = columns[_sortColumn].DefaultDescending;
		Name = "DataTable";
	}

	public int Limit { get; init; } = int.MaxValue;

	public Func<T, double>? ScaleBy { get; init; }

	public Action<T>? RowClicked { get; set; }

	public Action<T>? RowDoubleClicked { get; set; }

	public T? Selected => _selected;

	public int Count => _visible.Count;

	public void SetData(IReadOnlyList<T> rows) {
		_rows = rows;
		Rebuild();
	}

	public void SetFilter(Func<T, bool>? filter) {
		_filter = filter;
		Rebuild();
	}

	public void Select(T? row) {
		_selected = row;
		Repaint();
	}

	public void Refresh() => Rebuild();

	private void Rebuild() {
		_visible.Clear();

		foreach (T row in _rows) {
			if (_filter is null || _filter(row)) _visible.Add(row);
		}

		TableColumn<T> column = _columns[_sortColumn];
		Comparison<T> compare = column.Compare;

		if (_descending) _visible.Sort((left, right) => compare(right, left));
		else _visible.Sort(compare);

		if (_visible.Count > Limit) _visible.RemoveRange(Limit, _visible.Count - Limit);

		_scale = 1;

		if (ScaleBy is { } scaleBy) {
			foreach (T row in _visible) _scale = Math.Max(_scale, scaleBy(row));
		}

		_scroll = Math.Clamp(_scroll, 0f, MaxScroll());
		Repaint();
	}

	private float ContentHeight => _visible.Count * RowHeight * Scale;

	private float BodyHeight => MathF.Max(0f, Tall - (HeaderHeight * Scale));

	private float MaxScroll() => MathF.Max(0f, ContentHeight - BodyHeight);

	private int RowAt(float y) {
		float header = HeaderHeight * Scale;

		if (y < header) return -1;

		int index = (int)((y - header + _scroll) / (RowHeight * Scale));
		return (uint)index < (uint)_visible.Count ? index : -1;
	}

	private int GripAt(float x) {
		float scale = Scale;
		float edge = 0f;

		for (int i = 0; i < _columns.Length; i++) {
			edge += _columns[i].Width * scale;

			if (MathF.Abs(x - edge) <= GripWidth * scale / 2f) return i;
		}

		return -1;
	}

	private int ColumnAt(float x) {
		float scale = Scale;
		float edge = 0f;

		for (int i = 0; i < _columns.Length; i++) {
			edge += _columns[i].Width * scale;

			if (x < edge) return i;
		}

		return -1;
	}

	private bool OverScrollbar(float x) => MaxScroll() > 0f && x >= Wide - (ScrollbarWidth * Scale);

	public override void OnCursorMoved(float x, float y) {
		if (_resizing >= 0) {
			_columns[_resizing].Width = MathF.Max(40f, _resizeStartWidth + ((x - _resizeStartX) / Scale));
			Repaint();
			return;
		}

		if (_draggingThumb) {
			(float thumbTop, float thumbHeight) = Thumb();
			float track = BodyHeight - thumbHeight;
			float position = y - (HeaderHeight * Scale) - _thumbGrab;
			_scroll = track > 0f ? Math.Clamp(position / track, 0f, 1f) * MaxScroll() : 0f;
			Repaint();
			return;
		}

		Cursor = y < HeaderHeight * Scale && GripAt(x) >= 0 ? CursorCode.SizeWestEast : CursorCode.Arrow;
		int hover = OverScrollbar(x) ? -1 : RowAt(y);

		if (hover == _hover) return;

		_hover = hover;
		Repaint();
	}

	public override void OnCursorExited() {
		if (_hover < 0) return;

		_hover = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		float header = HeaderHeight * Scale;

		if (OverScrollbar(x) && y >= header) {
			(float thumbTop, float thumbHeight) = Thumb();
			float local = y - header;

			if (local < thumbTop || local > thumbTop + thumbHeight) {
				_scroll = Math.Clamp(_scroll + (local < thumbTop ? -BodyHeight : BodyHeight), 0f, MaxScroll());
				Repaint();
				return true;
			}

			_draggingThumb = true;
			_thumbGrab = local - thumbTop;
			Ui?.SetMouseCapture(this);
			return true;
		}

		if (y < header) {
			int grip = GripAt(x);

			if (grip >= 0) {
				_resizing = grip;
				_resizeStartX = x;
				_resizeStartWidth = _columns[grip].Width;
				Ui?.SetMouseCapture(this);
				return true;
			}

			int column = ColumnAt(x);

			if (column < 0) return true;

			if (column == _sortColumn) _descending = !_descending;
			else {
				_sortColumn = column;
				_descending = _columns[column].DefaultDescending;
			}

			Rebuild();
			return true;
		}

		int row = RowAt(y);

		if (row < 0) return true;

		_selected = _visible[row];
		Repaint();
		RowClicked?.Invoke(_selected);
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button == MouseButton.Left && y < HeaderHeight * Scale && GripAt(x) is var grip and >= 0) {
			_columns[grip].Width = _columns[grip].DefaultWidth;
			Repaint();
			return true;
		}

		if (button == MouseButton.Left && RowAt(y) is var row and >= 0 && RowDoubleClicked is { } doubleClicked) {
			doubleClicked(_visible[row]);
			return true;
		}

		return OnMousePressed(button, x, y);
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (_resizing < 0 && !_draggingThumb) return false;

		_resizing = -1;
		_draggingThumb = false;
		Ui?.SetMouseCapture(null);
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float next = Math.Clamp(_scroll - (delta / 120f * RowHeight * 3f * Scale), 0f, MaxScroll());

		if (next == _scroll) return false;

		_scroll = next;
		Repaint();
		return true;
	}

	private (float Top, float Height) Thumb() {
		float body = BodyHeight;
		float content = ContentHeight;

		if (content <= body || content <= 0f) return (0f, body);

		float height = MathF.Max(20f * Scale, body * body / content);
		return ((body - height) * (_scroll / MaxScroll()), height);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float header = HeaderHeight * scale;
		float rowHeight = RowHeight * scale;
		float padding = CellPadding * scale;
		ITextFont strong = painter.GetUi(12f, strong: true);

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		float x = ScreenX;

		for (int c = 0; c < _columns.Length; c++) {
			TableColumn<T> column = _columns[c];
			float width = column.Width * scale;
			string label = c == _sortColumn ? column.Label + (_descending ? " ▾" : " ▴") : column.Label;
			bool sorted = c == _sortColumn;
			float textX = column.Numeric ? x + width - padding - strong.Measure(label) : x + padding;

			painter.PushClip(x, ScreenY, width, header);
			painter.DrawText(strong, label, textX, ScreenY + ((header - strong.GetLineHeight()) / 2f) + strong.GetBaseline(), sorted ? Theme.EditorLinkActiveForeground : Theme.EditorForeground.WithAlpha(0.7f));
			painter.PopClip();
			x += width;
		}

		painter.DrawHorizontalLine(ScreenY + header - 1f, ScreenX, ScreenX + Wide, Theme.PanelBorder);
		painter.PushClip(ScreenX, ScreenY + header, Wide, Tall - header);

		int first = Math.Max(0, (int)(_scroll / rowHeight));

		for (int i = first; i < _visible.Count; i++) {
			float top = ScreenY + header + (i * rowHeight) - _scroll;

			if (top > ScreenY + Tall) break;

			if (!painter.NeedsPaint(ScreenX, top, Wide, rowHeight)) continue;

			T row = _visible[i];
			bool selected = ReferenceEquals(row, _selected);

			if (selected) painter.FillRectangle(ScreenX, top, Wide, rowHeight, Theme.ListActiveSelectionBackground);
			else if (i == _hover) painter.FillRectangle(ScreenX, top, Wide, rowHeight, Theme.ListHoverBackground);

			painter.DrawHorizontalLine(top + rowHeight - 1f, ScreenX, ScreenX + Wide, Theme.EditorForeground.WithAlpha(0.08f));

			float cellX = ScreenX;

			foreach (TableColumn<T> column in _columns) {
				float width = column.Width * scale;
				painter.PushClip(cellX + padding, top, MathF.Max(0f, width - (padding * 2f)), rowHeight);
				column.Paint(painter, row, new Cell(cellX + padding, top, width - (padding * 2f), rowHeight, _scale, selected));
				painter.PopClip();
				cellX += width;
			}
		}

		if (MaxScroll() > 0f) {
			(float thumbTop, float thumbHeight) = Thumb();
			float barWidth = ScrollbarWidth * scale;
			painter.FillRectangle(ScreenX + Wide - barWidth, ScreenY + header + thumbTop, barWidth, thumbHeight, _draggingThumb ? Theme.ScrollbarSliderActive : Theme.ScrollbarSlider);
		}

		painter.PopClip();
		painter.PopClip();
	}

	public static void Text(IPainter painter, ITextFont font, ReadOnlySpan<char> text, in Cell cell, Color color, bool right = false) {
		float x = right ? cell.X + cell.Width - font.Measure(text) : cell.X;
		painter.DrawText(font, text, x, cell.Y + ((cell.Height - font.GetLineHeight()) / 2f) + font.GetBaseline(), color);
	}

	public static void Bar(IPainter painter, double value, in Cell cell, Color color) {
		float width = (float)(cell.Width * Math.Clamp(value / cell.Scale, 0, 1));
		float height = 9f * Scale;
		painter.FillRoundedRectangle(cell.X, cell.Y + ((cell.Height - height) / 2f), width, height, 2f * Scale, color);
	}

	public static void FullBar(IPainter painter, double value, in Cell cell, Color color) {
		float width = (float)(cell.Width * Math.Clamp(value / cell.Scale, 0, 1));
		painter.FillRoundedRectangle(cell.X, cell.Y + (2f * Scale), width, cell.Height - (4f * Scale), 2f * Scale, color);
	}
}
