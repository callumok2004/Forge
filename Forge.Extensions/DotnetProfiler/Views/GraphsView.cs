using Forge.Common.Render;
using Forge.Common.UI;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class GraphsView : Panel
{
	private const float Padding = 10f;

	private readonly GenerationStrip _generations;
	private readonly MetricsGrid _grid;
	private float _scroll;

	public GraphsView(DotnetProfilerService service) {
		Name = "Live";

		_grid = new MetricsGrid(service, MetricsGrid.Full, ChartVariant.Card);
		_generations = new GenerationStrip(service);
		AddChild(_grid);
		AddChild(_generations);

		service.Changed += change => {
			if ((change & DotnetProfilerChange.Counters) == 0) return;

			_grid.Sync();
			_generations.Repaint();
		};
	}

	private float ContentHeight() {
		float width = Wide - (Padding * 2f * Scale);

		return (Padding * 2f * Scale) + _grid.ContentHeight(width) + _generations.ContentHeight(width);
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float next = Math.Clamp(_scroll - (delta / 120f * 48f * Scale), 0f, MathF.Max(0f, ContentHeight() - Tall));

		if (next == _scroll) return false;

		_scroll = next;
		InvalidateLayout();
		Repaint();
		return true;
	}

	public override void PerformLayout() {
		float padding = Padding * Scale;
		float width = Wide - (padding * 2f);
		float y = padding - _scroll;
		float grid = _grid.ContentHeight(width);

		_grid.SetBounds(padding, y, width, grid);
		_generations.SetBounds(padding, y + grid, width, _generations.ContentHeight(width));
	}

	public override void PaintBackground(IPainter painter) => painter.PushClip(ScreenX, ScreenY, Wide, Tall);

	public override void PostChildPaint(IPainter painter) => painter.PopClip();
}
