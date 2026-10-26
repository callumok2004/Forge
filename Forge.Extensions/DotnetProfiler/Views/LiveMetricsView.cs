using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class LiveMetricsView : Panel
{
	private readonly DotnetProfilerService _service;
	private readonly bool _panel;
	private readonly StatusPill _status;
	private readonly WebButton _record;
	private readonly WebButton _expand;
	private readonly Toggle _profileOnStart;
	private readonly TextEntry _filter;
	private readonly MetricsGrid _grid;
	private readonly ToolbarBar _bar = new();
	private float _scroll;
	private float _toolbarHeight;

	public LiveMetricsView(DotnetProfilerService service, bool panel) {
		_service = service;
		_panel = panel;
		Name = panel ? "dotnetProfiler.graphs" : "dotnetProfiler.live";

		_status = new StatusPill();
		_record = new WebButton("Record CPU", service.ToggleRecording, ButtonStyle.Secondary) { FontSize = 11f, PaddingX = panel ? 12f : 6f };
		_expand = new WebButton("Expand", service.OpenEditor) { FontSize = 11f, PaddingX = panel ? 12f : 6f };
		_profileOnStart = new Toggle("Start profiling on debug start") { Changed = service.SetProfileOnStart, FontSize = 11f };
		_filter = new TextEntry { Placeholder = "Limit to namespace (e.g. Profiler.Host.*)", FontSize = 11f };
		_filter.Submitted = () => service.SetNamespaceFilter(_filter.GetString());
		_grid = new MetricsGrid(service, MetricsGrid.Compact, panel ? ChartVariant.Panel : ChartVariant.Mini);

		AddChild(_grid);
		AddChild(_bar);

		foreach (Panel child in (ReadOnlySpan<Panel>)[_status, _record, _expand, _profileOnStart, _filter]) _bar.AddChild(child);

		service.Changed += Sync;
		Sync(DotnetProfilerChange.Session | DotnetProfilerChange.Options | DotnetProfilerChange.Recording);
	}

	public void FocusFilter() {
		_filter.RequestFocus();
		_filter.SelectAll();
	}

	private void Sync(DotnetProfilerChange change) {
		if ((change & DotnetProfilerChange.Counters) != 0) _grid.Sync();

		if ((change & (DotnetProfilerChange.Session | DotnetProfilerChange.Status)) != 0) _status.Set(_service.Live ? _service.Status : "no session", _service.Live);

		if ((change & (DotnetProfilerChange.Recording | DotnetProfilerChange.Session)) != 0) {
			_record.Text = _service.Recording ? "Stop" : "Record CPU";
			_record.Style = _service.Recording ? ButtonStyle.Recording : ButtonStyle.Secondary;
		}

		if ((change & DotnetProfilerChange.Options) != 0) {
			_profileOnStart.Checked = _service.ProfileOnStart;

			if (!_filter.HasFocus) _filter.SetText(_service.Options.NamespaceFilter);
		}

		if ((change & ~DotnetProfilerChange.Counters) != 0) InvalidateLayout();
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float max = MathF.Max(0f, ContentHeight() - Tall);
		float next = Math.Clamp(_scroll - (delta / 120f * 48f * Scale), 0f, max);

		if (next == _scroll) return false;

		_scroll = next;
		InvalidateLayout();
		Repaint();
		return true;
	}

	private float GridPadding => (_panel ? 12f : 10f) * Scale;

	private float ContentHeight() => _toolbarHeight + ((_panel ? 10f + 16f : 8f + 14f) * Scale) + _grid.ContentHeight(Wide - (GridPadding * 2f));

	public override void PerformLayout() {
		float scale = Scale;
		float control = 22f * scale;
		float gap = 6f * scale;

		if (_panel) {
			float padX = 12f * scale;
			float y = 6f * scale;
			_toolbarHeight = control + (12f * scale);
			_status.SetBounds(padX, y + (1f * scale), _status.PreferredWidth(scale), control - (2f * scale));

			float right = Wide - padX;
			float filterWidth = 220f * scale;
			_filter.SetBounds(right - filterWidth, y, filterWidth, control);
			right -= filterWidth + (10f * scale);
			float toggleWidth = Ui is null ? 200f * scale : _profileOnStart.PreferredWidth(Ui.GetFonts().GetUi(11f), scale);
			_profileOnStart.SetBounds(right - toggleWidth, y, toggleWidth, control);

			float x = padX + _status.Wide + (10f * scale);
			_record.SetBounds(x, y, _record.PreferredWidth(scale), control);
			x += _record.Wide + gap;
			_expand.SetBounds(x, y, _expand.PreferredWidth(scale), control);
		}
		else {
			float padX = 10f * scale;
			float width = Wide - (padX * 2f);
			float y = 8f * scale;

			_status.SetBounds(padX, y, MathF.Min(width, _status.PreferredWidth(scale)), 20f * scale);
			y += (20f * scale) + gap;

			float half = (width - gap) / 2f;
			_record.SetBounds(padX, y, half, control);
			_expand.SetBounds(padX + half + gap, y, half, control);
			y += control + gap;

			_profileOnStart.SetBounds(padX, y, width, 18f * scale);
			y += (18f * scale) + (5f * scale);

			_filter.SetBounds(padX, y, width, control);
			y += control + (6f * scale);

			_toolbarHeight = y;
		}

		_bar.SetBounds(0f, 0f, Wide, _toolbarHeight);

		float gridTop = _toolbarHeight + ((_panel ? 10f : 8f) * scale) - _scroll;
		float gridWidth = Wide - (GridPadding * 2f);
		_grid.SetBounds(GridPadding, gridTop, gridWidth, _grid.ContentHeight(gridWidth));
	}

	public override void PaintBackground(IPainter painter) => painter.PushClip(ScreenX, ScreenY, Wide, Tall);

	public override void PostChildPaint(IPainter painter) => painter.PopClip();

	private sealed class ToolbarBar : Panel
	{
		public override void PaintBackground(IPainter painter) => painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.ChromeBackground);

		public override void PaintBorder(IPainter painter) => painter.DrawHorizontalLine(ScreenY + Tall - 1f, ScreenX, ScreenX + Wide, Theme.PanelBorder);
	}
}
