using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Views;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Extensions.DotnetProfiler;

public sealed class DotnetProfilerEditor : EditorPane
{
	private static readonly string[] TabLabels = ["Live", "Top Functions", "Flame Graph", "Call Tree", "Allocations", "Memory"];
	private static readonly int[] Intervals = [100, 250, 500, 1000, 2000];
	private static readonly string[] IntervalLabels = ["100 ms", "250 ms", "500 ms", "1 s", "2 s"];

	private readonly DotnetProfilerService _service;
	private readonly ProfileViewState _state;
	private readonly FlowRow _toolbar;
	private readonly StatusPill _status;
	private readonly Label _intervalLabel;
	private readonly SelectBox _interval;
	private readonly WebButton _record;
	private readonly WebButton _snapshot;
	private readonly WebButton _detach;
	private readonly TabBar _tabs;
	private readonly Panel[] _pages;
	private readonly FunctionsTab _functions;
	private readonly CallTreePage _tree;
	private readonly FlamePage _flame;

	public DotnetProfilerEditor(DotnetProfilerService service, ICommandHost host, Action<string> openFrame) {
		_service = service;
		_state = service.View;
		Name = DotnetProfilerService.EditorId;

		_toolbar = new FlowRow { WidthOf = ToolbarWidth, Border = true };
		_status = new StatusPill();
		_intervalLabel = new Label { Foreground = Theme.EditorForeground.WithAlpha(0.8f), FontSize = 11f };
		_intervalLabel.SetText("refresh");
		_interval = new SelectBox(IntervalLabels) { Changed = index => service.SetInterval(Intervals[index]) };
		_record = new WebButton("Record CPU", service.ToggleRecording, ButtonStyle.Primary);
		_snapshot = new WebButton("Heap Dump", () => _ = service.SnapshotAsync());
		_detach = new WebButton("Detach", () => service.Detach());

		_toolbar.AddChild(_status);

		foreach (Panel item in (ReadOnlySpan<Panel>)[_detach, _snapshot, _record, _interval, _intervalLabel]) {
			_toolbar.AddChild(item);
			_toolbar.RightAligned.Add(item);
		}

		_tabs = new TabBar(TabLabels) { Changed = Select };
		_functions = new FunctionsTab(_state, openFrame, ShowInFlame);
		_tree = new CallTreePage(_state, openFrame);
		_flame = new FlamePage(_state, openFrame);
		_pages = [new GraphsView(service), _functions, _flame, _tree, new AllocationsTab(_state), new HeapTab(service)];

		AddChild(_toolbar);
		AddChild(_tabs);

		foreach (Panel page in _pages) AddChild(page);

		service.Changed += OnChanged;
		Sync(DotnetProfilerChange.Session | DotnetProfilerChange.Recording | DotnetProfilerChange.Options);
		Select(0);
	}

	public override string Title => ".NET Profiler";

	public override Codicon Icon => Codicon.Pulse;

	private float ToolbarWidth(Panel child) {
		float scale = Scale;

		return child switch {
			StatusPill pill => pill.PreferredWidth(scale),
			SelectBox select => select.PreferredWidth(scale),
			WebButton button => button.PreferredWidth(scale),
			Label label when Ui is { } ui => label.MeasureContents(ui.GetFonts().GetUi(11f), scale),
			_ => 100f * scale,
		};
	}

	private void OnChanged(DotnetProfilerChange change) {
		if ((change & DotnetProfilerChange.Profile) != 0) Select(1);

		Sync(change);
	}

	private void Sync(DotnetProfilerChange change) {
		if ((change & (DotnetProfilerChange.Session | DotnetProfilerChange.Status)) != 0) _status.Set(_service.Live ? _service.Status : "no session", _service.Live);

		if ((change & (DotnetProfilerChange.Session | DotnetProfilerChange.Recording)) != 0) {
			_record.Text = _service.Recording ? "Stop Recording" : "Record CPU";
			_record.Style = _service.Recording ? ButtonStyle.Recording : ButtonStyle.Primary;
			_record.Enabled = _snapshot.Enabled = _detach.Enabled = _service.Live;
		}

		if ((change & DotnetProfilerChange.Options) != 0) _interval.Selected = Math.Max(0, Array.IndexOf(Intervals, _service.IntervalMs));

		_toolbar.InvalidateLayout();
		_toolbar.Repaint();
	}

	private void Select(int index) {
		for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;

		_tabs.Selected = index;
	}

	private void ShowInFlame(Metric metric, string name) {
		_flame.Show(metric, name);
		Select(2);
	}

	public override void OnClosed() => _service.Changed -= OnChanged;

	public override void PerformLayout() {
		float scale = Scale;
		float toolbar = FlowRow.PreferredHeight(scale);
		float tabs = TabBar.PreferredHeight(scale);

		_toolbar.SetBounds(0f, 0f, Wide, toolbar);
		_tabs.SetBounds(0f, toolbar, Wide, tabs);

		foreach (Panel page in _pages) page.SetBounds(0f, toolbar + tabs, Wide, Tall - toolbar - tabs);
	}

	public override void PaintBackground(IPainter painter) => painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.EditorBackground);

	private static float ItemWidth(Panel child, float scale) {
		if (Panel.Ui is not { } ui) return 100f * scale;

		IFontSet fonts = ui.GetFonts();

		return child switch {
			Segmented segmented => segmented.PreferredWidth(fonts.GetUi(segmented.FontSize), scale),
			Toggle toggle => toggle.PreferredWidth(fonts.GetUi(toggle.FontSize), scale),
			WebButton button => button.PreferredWidth(scale),
			TextEntry => 220f * scale,
			Label label => label.MeasureContents(fonts.GetUi(12f), scale),
			_ => 100f * scale,
		};
	}

	private static Label InfoLabel() => new() { FontSize = 12f, Foreground = Theme.EditorForeground.WithAlpha(0.7f), Alignment = TextAlignment.Right };

	private sealed class CallTreePage : Panel
	{
		private readonly ProfileViewState _state;
		private readonly FlowRow _toolbar;
		private readonly Segmented _metric;
		private readonly Label _info;
		private readonly CallTreeView _view;

		public CallTreePage(ProfileViewState state, Action<string> openFrame) {
			_state = state;
			_view = new CallTreeView { OpenFrame = openFrame };
			_toolbar = new FlowRow { WidthOf = child => ItemWidth(child, Scale) };
			_metric = new Segmented("CPU samples", "Allocated bytes") { Changed = _ => Refresh() };
			_info = InfoLabel();

			_toolbar.AddChild(_metric);
			_toolbar.AddChild(new WebButton("Expand hot path", _view.ExpandHotPath));
			_toolbar.AddChild(new WebButton("Collapse all", _view.CollapseAll));
			_toolbar.AddChild(_info);
			_toolbar.Fill = _info;

			AddChild(_toolbar);
			AddChild(_view);

			state.Changed += Refresh;
			Refresh();
		}

		private void Refresh() {
			Metric metric = (Metric)_metric.Selected;
			_view.SetData(_state.Tree(metric));
			_info.SetText(_state.Index(metric) is { } index ? $"{_state.Format(metric, index.RootTotal)} {(metric == Metric.Cpu ? "samples" : "bytes")} · click a row to expand" : string.Empty);
		}

		public override void PerformLayout() {
			float toolbar = FlowRow.PreferredHeight(Scale);
			_toolbar.SetBounds(0f, 0f, Wide, toolbar);
			_view.SetBounds(0f, toolbar + (6f * Scale), Wide, Tall - toolbar - (6f * Scale));
		}
	}

	private sealed class FlamePage : Panel
	{
		private readonly ProfileViewState _state;
		private readonly FlowRow _toolbar;
		private readonly Segmented _metric;
		private readonly TextEntry _filter;
		private readonly Label _info;
		private readonly FlameGraph _graph;

		public FlamePage(ProfileViewState state, Action<string> openFrame) {
			_state = state;
			_graph = new FlameGraph { OpenFrame = openFrame };
			_toolbar = new FlowRow { WidthOf = child => ItemWidth(child, Scale) };
			_metric = new Segmented("CPU samples", "Allocated bytes") { Changed = _ => Refresh() };
			_filter = new TextEntry { Placeholder = "Filter frames (regex)", FontSize = 12f };
			_filter.TextChanged = () => _graph.SetFilter(_filter.GetString());
			Toggle system = new("show system frames") { Changed = value => _graph.ShowSystem = value };
			_info = InfoLabel();

			foreach (Panel child in (ReadOnlySpan<Panel>)[_metric, _filter, system, new WebButton("Reset zoom", _graph.ResetZoom), _info]) _toolbar.AddChild(child);

			_toolbar.Fill = _info;

			AddChild(_toolbar);
			AddChild(_graph);

			state.Changed += Refresh;
			Refresh();
		}

		public void Show(Metric metric, string name) {
			_metric.Selected = (int)metric;
			Refresh();
			_graph.FocusOnName(name);
		}

		private void Refresh() {
			Metric metric = (Metric)_metric.Selected;
			_graph.SetData(_state.Tree(metric), value => _state.Format(metric, value));

			string limit = _state.Profile?.NamespaceFilter is { Length: > 0 } filter ? $" · limited to {filter}" : string.Empty;
			_info.SetText(_state.Profile is { } profile ? metric == Metric.Cpu ? $"{profile.TotalSamples:N0} samples over {profile.DurationMs / 1000:0.0}s{limit}" : $"{Engine.Format.Bytes(profile.TotalAllocBytes)} bytes{limit}" : string.Empty);
		}

		public override void PerformLayout() {
			float toolbar = FlowRow.PreferredHeight(Scale);
			_toolbar.SetBounds(0f, 0f, Wide, toolbar);
			_graph.SetBounds(0f, toolbar, Wide, Tall - toolbar);
		}
	}
}
