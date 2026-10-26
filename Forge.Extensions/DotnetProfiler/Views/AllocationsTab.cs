using Forge.Common.UI;
using Forge.UI.Controls;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class AllocationsTab : Panel
{
	private readonly ProfileViewState _state;
	private readonly DataTable<AllocSite> _table;
	private readonly DetailView _detail;
	private readonly SplitPane _split;
	private readonly FlowRow _toolbar;

	public AllocationsTab(ProfileViewState state) {
		_state = state;
		Name = "Allocations";

		_table = new DataTable<AllocSite>([
			new("type", "Type", 360f, static (left, right) => string.CompareOrdinal(left.TypeName, right.TypeName), static (painter, row, in cell) => DataTable<AllocSite>.Text(painter, painter.GetMono(12f), row.TypeName, cell, Theme.EditorForeground)) { DefaultDescending = false },
			new("bytes", "Allocated", 100f, static (left, right) => left.Bytes.CompareTo(right.Bytes), static (painter, row, in cell) => DataTable<AllocSite>.Text(painter, painter.GetUi(12f), Engine.Format.Bytes(row.Bytes), cell, Theme.EditorForeground, right: true)) { Numeric = true },
			new("count", "Ticks", 80f, static (left, right) => left.Count.CompareTo(right.Count), static (painter, row, in cell) => DataTable<AllocSite>.Text(painter, painter.GetUi(12f), Engine.Format.Number(row.Count), cell, Theme.EditorForeground, right: true)) { Numeric = true },
			new("site", "Top Site", 260f, static (left, right) => string.CompareOrdinal(left.TopFrame, right.TopFrame), static (painter, row, in cell) => DataTable<AllocSite>.Text(painter, painter.GetMono(12f), row.TopFrame is { } frame ? FrameNames.Short(frame) : "–", cell, Theme.DescriptionForeground)) { DefaultDescending = false },
			new("share", "Share", 140f, static (left, right) => left.Bytes.CompareTo(right.Bytes), static (painter, row, in cell) => DataTable<AllocSite>.Bar(painter, row.Bytes, cell, Theme.ChartsYellow.WithAlpha(0.5f))),
		], "bytes") { ScaleBy = static row => row.Bytes, RowClicked = ShowDetail };

		_detail = new DetailView();
		_split = new SplitPane(_table, _detail);

		Toggle signatures = new("full signatures") { Changed = value => {
			state.FullSignatures = value;
			state.Notify();
		} };
		Label hint = new() { FontSize = 11.5f, Foreground = Theme.EditorForeground.WithAlpha(0.65f) };
		hint.SetText("Sampled from GCAllocationTick, roughly one sample per 100 KB allocated. Proportions are reliable; totals are an estimate.");
		_toolbar = new FlowRow { WidthOf = child => child is Toggle toggle && Ui is { } ui ? toggle.PreferredWidth(ui.GetFonts().GetUi(toggle.FontSize), Scale) : 100f * Scale };
		_toolbar.AddChild(signatures);
		_toolbar.AddChild(hint);
		_toolbar.Fill = hint;

		AddChild(_toolbar);
		AddChild(_split);

		state.Changed += Refresh;
		Refresh();
	}

	private void Refresh() {
		_detail.FullSignatures = _state.FullSignatures;
		_table.SetData(_state.Profile?.Sites ?? []);
		_detail.Message(_state.Profile is null ? "Record CPU with allocation tracking on to see what allocates." : "Select a type to see where it is allocated.");
	}

	private void ShowDetail(AllocSite site) {
		if (site.Stacks is not { Count: > 0 } stacks) {
			_detail.Show([
				new DetailView.Heading(site.TypeName),
				new DetailView.Hint("No call stacks were captured for this type. Only the top 50 types by size keep stack detail, and ticks recorded before the runtime had rundown data can lack stacks."),
			]);
			return;
		}

		double total = Math.Max(1, site.Bytes);
		List<DetailView.Block> blocks = [
			new DetailView.Heading(site.TypeName),
			new DetailView.Hint($"{Engine.Format.Bytes(site.Bytes)} across {Engine.Format.Number(site.Count)} ticks · top {stacks.Count} allocation {(stacks.Count == 1 ? "site" : "sites")}"),
		];

		foreach (AllocStack stack in stacks) blocks.Add(new DetailView.Stack(Engine.Format.Bytes(stack.Bytes), $"{stack.Bytes / total * 100:0.0}%", $"{Engine.Format.Number(stack.Count)} ticks", stack.Frames));

		_detail.Show(blocks);
	}

	public override void PerformLayout() {
		float toolbar = FlowRow.PreferredHeight(Scale);
		_toolbar.SetBounds(0f, 0f, Wide, toolbar);
		_split.SetBounds(0f, toolbar, Wide, Tall - toolbar);
	}
}
