using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class FunctionsTab : Panel
{
	private readonly ProfileViewState _state;
	private readonly Action<string> _openFrame;
	private readonly Action<Metric, string> _showInFlame;
	private readonly FlowRow _toolbar;
	private readonly Segmented _metric;
	private readonly TextEntry _filter;
	private readonly Toggle _system;
	private readonly Toggle _group;
	private readonly Segmented _unit;
	private readonly TextEntry _fps;
	private readonly WebButton _baseline;
	private readonly Label _at;
	private readonly Label _fpsLabel;
	private readonly Toggle _signatures;
	private readonly Label _info;
	private readonly DataTable<FunctionRow> _table;
	private readonly DetailView _detail;
	private readonly SplitPane _split;

	private static readonly Color HotBar = Color.Parse("#e06c75");

	private Metric _current = Metric.Cpu;
	private string _query = string.Empty;
	private bool _showSystem;
	private FunctionRow? _selected;

	public FunctionsTab(ProfileViewState state, Action<string> openFrame, Action<Metric, string> showInFlame) {
		_state = state;
		_openFrame = openFrame;
		_showInFlame = showInFlame;
		Name = "Functions";

		_toolbar = new FlowRow { WidthOf = WidthOf };
		_metric = new Segmented("CPU samples", "Allocated bytes") { Changed = index => SetMetric((Metric)index) };
		_filter = new TextEntry { Placeholder = "Filter functions", FontSize = 12f };
		_filter.TextChanged = () => {
			_query = _filter.GetString().Trim();
			_table!.SetFilter(Filter);
		};
		_system = new Toggle("show system frames") { Changed = value => {
			_showSystem = value;
			_table!.SetFilter(Filter);
		} };
		_group = new Toggle("group lambdas/generics") { Checked = state.Grouped, Changed = state.SetGrouped };
		_unit = new Segmented("time (ms)", "ms per second", "per frame", "raw samples") { Selected = (int)state.Unit, Changed = index => {
			state.Unit = (Unit)index;
			_fps!.Enabled = state.Unit == Unit.PerFrame;
			_fps.Repaint();
			state.Notify();
		} };
		_fps = new TextEntry { Enabled = state.Unit == Unit.PerFrame, FontSize = 12f };
		_at = new Label { FontSize = 12f, Foreground = Theme.EditorForeground };
		_at.SetText("@");
		_fpsLabel = new Label { FontSize = 12f, Foreground = Theme.EditorForeground };
		_fpsLabel.SetText("fps");
		_fps.SetText(state.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture));
		_fps.Submitted = () => {
			state.Fps = int.TryParse(_fps.Text, out int fps) && fps > 0 ? fps : 60;
			state.Notify();
		};
		_baseline = new WebButton("Set as baseline", ToggleBaseline);
		_signatures = new Toggle("full signatures") { Changed = value => {
			state.FullSignatures = value;
			_detail!.FullSignatures = value;
			Refresh();
		} };

		_info = new Label { FontSize = 12f, Foreground = Theme.EditorForeground.WithAlpha(0.7f), Alignment = TextAlignment.Right };

		foreach (Panel child in (ReadOnlySpan<Panel>)[_metric, _unit, _at, _fps, _fpsLabel, _filter, _group, _system, _signatures, _baseline, _info]) _toolbar.AddChild(child);

		_toolbar.Fill = _info;

		_table = new DataTable<FunctionRow>(Columns(), "own") { Limit = 500, ScaleBy = static row => row.Own, RowClicked = ShowDetail, RowDoubleClicked = row => openFrame(row.Name) };
		_detail = new DetailView();
		_split = new SplitPane(_table, _detail);

		AddChild(_toolbar);
		AddChild(_split);

		state.Changed += Refresh;
		Refresh();
	}

	private float WidthOf(Panel child) {
		if (Ui is null) return 100f;

		IFontSet fonts = Ui.GetFonts();
		float scale = Scale;

		return child switch {
			Segmented segmented => segmented.PreferredWidth(fonts.GetUi(segmented.FontSize), scale),
			Toggle toggle => toggle.PreferredWidth(fonts.GetUi(toggle.FontSize), scale),
			TextEntry when ReferenceEquals(child, _fps) => 48f * scale,
			TextEntry => 200f * scale,
			WebButton button => button.PreferredWidth(scale),
			Label label => label.MeasureContents(fonts.GetUi(12f), scale),
			_ => 100f * scale,
		};
	}

	private TableColumn<FunctionRow>[] Columns() => [
		new("own", "Own", 150f, static (left, right) => left.Own.CompareTo(right.Own), (painter, row, in cell) => {
			bool lowConfidence = _current == Metric.Cpu && Math.Max(row.Own, row.Self) < ProfileViewState.LowConfidence;
			float alpha = lowConfidence ? 0.45f : 1f;
			DataTable<FunctionRow>.FullBar(painter, row.Own, cell, HotBar.WithAlpha(0.33f * alpha));
			ITextFont pct = painter.GetUi(10.5f);
			string percent = _state.Percent(_current, row.Own);
			DataTable<FunctionRow>.Text(painter, pct, percent, cell, Theme.EditorForeground.WithAlpha(0.75f * alpha), right: true);
			DataTable<FunctionRow>.Text(painter, painter.GetUi(12f, strong: true), _state.Format(_current, row.Own), cell with { X = cell.X + (6f * Scale) }, Theme.EditorForeground.WithAlpha(alpha));
		}) { Numeric = true },
		new("self", "Self", 120f, static (left, right) => left.Self.CompareTo(right.Self), (painter, row, in cell) => DataTable<FunctionRow>.Text(painter, painter.GetUi(12f), $"{_state.Format(_current, row.Self)}  {_state.Percent(_current, row.Self)}", cell, Theme.EditorForeground, right: true)) { Numeric = true },
		new("total", "Total", 120f, static (left, right) => left.Total.CompareTo(right.Total), (painter, row, in cell) => DataTable<FunctionRow>.Text(painter, painter.GetUi(12f), $"{_state.Format(_current, row.Total)}  {_state.Percent(_current, row.Total)}", cell, Theme.EditorForeground, right: true)) { Numeric = true },
		new("alloc", "Alloc", 90f, (left, right) => _state.AllocOwn(left).CompareTo(_state.AllocOwn(right)), (painter, row, in cell) => {
			long bytes = _current == Metric.Cpu ? _state.AllocOwn(row) : 0;
			DataTable<FunctionRow>.Text(painter, painter.GetUi(12f), bytes > 0 ? _state.FormatValue(Metric.Alloc, _state.Convert(Metric.Alloc, bytes)) : "–", cell, bytes > 0 ? Theme.EditorForeground : Theme.DescriptionForeground, right: true);
		}) { Numeric = true },
		new("delta", "Δ Baseline", 100f, (left, right) => (_state.BaselineDelta(_current, left) ?? 0).CompareTo(_state.BaselineDelta(_current, right) ?? 0), (painter, row, in cell) => {
			double? delta = _state.BaselineDelta(_current, row);

			if (delta is not { } value) {
				DataTable<FunctionRow>.Text(painter, painter.GetUi(12f), "–", cell, Theme.DescriptionForeground, right: true);
				return;
			}

			if (Math.Abs(value) < 0.5) {
				DataTable<FunctionRow>.Text(painter, painter.GetUi(12f), "0", cell, Theme.DescriptionForeground, right: true);
				return;
			}

			DataTable<FunctionRow>.Text(painter, painter.GetUi(12f), (value > 0 ? "+" : "−") + _state.Format(_current, Math.Abs(value)), cell, value > 0 ? Theme.ErrorForeground : Theme.SuccessForeground, right: true);
		}) { Numeric = true },
		new("name", "Function", 420f, static (left, right) => string.CompareOrdinal(left.Name, right.Name), (painter, row, in cell) => DataTable<FunctionRow>.Text(painter, painter.GetMono(12f), FrameNames.Display(row.Name, _state.FullSignatures), cell, row.System ? Theme.DescriptionForeground : Theme.EditorForeground)) { DefaultDescending = false },
		new("module", "Module", 160f, static (left, right) => string.CompareOrdinal(left.Module, right.Module), (painter, row, in cell) => DataTable<FunctionRow>.Text(painter, painter.GetUi(12f), row.Module.Length > 0 ? row.Module : "–", cell, Theme.DescriptionForeground)) { DefaultDescending = false },
	];

	private bool Filter(FunctionRow row) {
		if (!_showSystem && row.System) return false;

		return _query.Length == 0 || row.Name.Contains(_query, StringComparison.OrdinalIgnoreCase) || row.Module.Contains(_query, StringComparison.OrdinalIgnoreCase);
	}

	public void SetMetric(Metric metric) {
		_current = metric;
		_metric.Selected = (int)metric;
		Refresh();
	}

	private void ToggleBaseline() {
		_state.Baseline = _state.Baseline is null && _state.Cpu is not null ? new Baseline(_state.Cpu, _state.Alloc, _state.DurationSeconds, $"{DateTime.Now:T} ({_state.DurationSeconds:0.0}s)") : null;
		_state.Notify();
	}

	private void Refresh() {
		FunctionIndex? index = _state.Index(_current);

		_baseline.Text = _state.Baseline is null ? "Set as baseline" : "Clear baseline";
		_baseline.Enabled = _state.Baseline is not null || _state.Cpu is not null;
		_table.SetFilter(Filter);
		_table.SetData(index?.Rows ?? (IReadOnlyList<FunctionRow>)[]);

		if (index is null) {
			_info.SetText(string.Empty);
			_detail.Message("Record CPU, then select a function to see who calls it, what it calls, and the call paths that reach it.");
			return;
		}

		string limit = _state.Profile?.NamespaceFilter is { Length: > 0 } filter ? $" · limited to {filter}" : string.Empty;
		string baseline = _state.Baseline is { } set ? $" · baseline: {set.Label}" : string.Empty;
		_info.SetText($"{_state.Format(_current, index.RootTotal)} total · {_state.DurationSeconds:0.0}s · {_state.UnitHint(_current)}{baseline}{limit}");

		if (_selected is not null && index.Get(_selected.Key) is { } again) ShowDetail(again);
		else _detail.Message("Select a function to see who calls it, what it calls, and the call paths that reach it.");
	}

	private void ShowDetail(FunctionRow row) {
		_selected = row;
		_table.Select(row);

		if (_state.Index(_current) is not { } index) return;

		double root = Math.Max(1, index.RootTotal);
		long allocOwn = _current == Metric.Cpu ? _state.AllocOwn(row) : 0;
		List<(string, string, string)> kpis = [
			("Own", _state.Format(_current, row.Own), $"{row.Own / root * 100:0.00}%"),
			("Self", _state.Format(_current, row.Self), $"{row.Self / root * 100:0.00}%"),
			("Total", _state.Format(_current, row.Total), $"{row.Total / root * 100:0.00}%"),
		];

		if (_current == Metric.Cpu) kpis.Add(("Alloc", allocOwn > 0 ? _state.FormatValue(Metric.Alloc, _state.Convert(Metric.Alloc, allocOwn)) : "–", string.Empty));

		List<DetailView.Block> blocks = [
			new DetailView.Heading(row.Name),
			new DetailView.Hint(row.Module.Length > 0 ? row.Module : "unknown module"),
			new DetailView.Kpis(kpis),
			Verdict(row),
			new DetailView.Buttons([("Go to source", () => _openFrame(row.Name)), ("Show in flame graph", () => _showInFlame(_current, FlameName(index, row)))]),
			new DetailView.Edges("Callers", "who calls it · share of its total", Edges(index, index.Callers(row), row.Total)),
			new DetailView.Edges("Callees", "where its total goes", Edges(index, index.Callees(row), row.Total)),
			new DetailView.Section("Hottest call paths"),
		];

		foreach (CallPath path in index.CallPaths(row)) blocks.Add(new DetailView.Stack($"self {_state.Format(_current, path.Self)}", $"{path.Total / root * 100:0.0}%", $"total {_state.Format(_current, path.Total)}", path.Frames));

		_detail.Show(blocks);
	}

	private static string FlameName(FunctionIndex index, FunctionRow row) => row.Nodes.Count > 0 ? index.Tree.Name(row.Nodes[0]) : row.Name;

	private List<DetailView.EdgeItem> Edges(FunctionIndex index, List<Edge> edges, long denominator) {
		List<DetailView.EdgeItem> items = new(edges.Count);
		double total = Math.Max(1, denominator);

		foreach (Edge edge in edges) {
			Action? click = edge.Row >= 0 && edge.Name != FunctionIndex.SelfName ? () => ShowDetail(index.Rows[edge.Row]) : null;
			items.Add(new DetailView.EdgeItem(edge.Weight / total, _state.Format(_current, edge.Weight), edge.Name, FrameNames.IsSystem(edge.Name, edge.Module), click));
		}

		return items;
	}

	private DetailView.Verdict Verdict(FunctionRow row) {
		double share = row.Own / (double)Math.Max(1, row.Total);
		int percent = (int)Math.Round(share * 100);

		if (_current == Metric.Cpu && row.Total < ProfileViewState.LowConfidence) return new DetailView.Verdict("Too few samples.", "Record longer or under more load before trusting this row.");

		if (share > 0.7) return new DetailView.Verdict("The cost is inside this function.", $"{percent}% of its total is its own code or direct framework calls. Optimise its body.");

		if (share < 0.2) return new DetailView.Verdict("The cost is in what it calls.", $"Only {percent}% is its own work. Follow the top callee below.");

		return new DetailView.Verdict("Mixed.", $"{percent}% own work, the rest in callees. Check both.");
	}

	public override void PerformLayout() {
		float scale = Scale;
		float toolbar = FlowRow.PreferredHeight(scale);

		_toolbar.SetBounds(0f, 0f, Wide, toolbar);
		_split.SetBounds(0f, toolbar, Wide, Tall - toolbar);
	}
}
