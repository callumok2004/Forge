using Forge.Common.Render;
using Forge.Core;
using Forge.Theming;

namespace Forge.UI;

public sealed class LaunchPicker
{
	private const float RowHeight = 26f;
	private const float MinWidth = 220f;
	private const float PaddingY = 6f;

	private float _left;
	private float _top;
	private float _width;
	private float _height;
	private int _hover = -1;

	public bool IsOpen { get; private set; }

	public IReadOnlyList<string> Items { get; private set; } = [];

	public string? Selected { get; set; }

	public void Open(IReadOnlyList<string> items, float left, float top) {
		Items = items;
		IsOpen = items.Count > 0;
		_left = left;
		_top = top;
		_hover = -1;
	}

	public void Close() {
		IsOpen = false;
		_hover = -1;
	}

	public PixelRect Bounds => IsOpen
		? new PixelRect((int)_left - 2, (int)_top - 2, (int)(_left + _width) + 2, (int)(_top + _height) + 2)
		: default;

	public bool Contains(float x, float y) =>
		IsOpen && x >= _left && x < _left + _width && y >= _top && y < _top + _height;

	public bool Hover(float x, float y, float scale) {
		int row = RowAt(x, y, scale);

		if (row == _hover) return false;

		_hover = row;
		return true;
	}

	public string? Accept(float x, float y, float scale) {
		int row = RowAt(x, y, scale);

		return row >= 0 && row < Items.Count ? Items[row] : null;
	}

	private int RowAt(float x, float y, float scale) {
		if (!Contains(x, y)) return -1;

		int row = (int)((y - _top - (PaddingY * scale)) / (RowHeight * scale));

		return row >= 0 && row < Items.Count ? row : -1;
	}

	public void Render(IPainter painter, float scale) {
		if (!IsOpen) return;

		ITextFont font = painter.GetUi();

		float rowHeight = RowHeight * scale;
		float padding = PaddingY * scale;
		float widest = MinWidth * scale;

		foreach (string item in Items) {
			widest = Math.Max(widest, font.Measure(item) + (Metrics.Space16 * 3f * scale));
		}

		_width = widest;
		_height = (Items.Count * rowHeight) + (padding * 2f);

		float radius = Metrics.RadiusSmall * scale;

		painter.FillRoundedRectangle(_left, _top, _width, _height, radius, Theme.ChromeBackground);
		painter.StrokeRoundedRectangle(_left, _top, _width, _height, radius,
			Metrics.StrokeThickness * scale, Theme.PanelBorder);

		for (int i = 0; i < Items.Count; i++) {
			float rowTop = _top + padding + (i * rowHeight);
			bool selected = string.Equals(Items[i], Selected, StringComparison.Ordinal);

			if (i == _hover) {
				painter.FillRectangle(_left, rowTop, _width, rowHeight, Theme.ListHoverBackground);
			}

			if (selected) {
				painter.DrawIcon(Codicon.Check, _left + (Metrics.Space12 * scale) - (2f * scale),
					rowTop + (rowHeight / 2f), Metrics.IconSize * scale, Theme.Accent);
			}

			painter.DrawText(font, Items[i], _left + (Metrics.Space16 * 2f * scale),
				rowTop + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline(),
				i == _hover ? Theme.TabActiveForeground : Theme.ChromeForeground);
		}
	}
}
