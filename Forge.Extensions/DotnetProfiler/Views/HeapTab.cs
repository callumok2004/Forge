using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class HeapTab : Panel
{
	private static readonly Dictionary<string, Color> SegmentColors = new(StringComparer.Ordinal) {
		["gen0"] = Color.Parse("#4ec9b0"),
		["gen1"] = Color.Parse("#dcb45a"),
		["gen2"] = Color.Parse("#e06c75"),
		["loh"] = Color.Parse("#569cd6"),
		["poh"] = Color.Parse("#c586c0"),
	};

	private readonly DotnetProfilerService _service;
	private readonly FlowRow _toolbar;
	private readonly WebButton _take;
	private readonly WebButton _compare;
	private readonly SelectBox _pick;
	private readonly Label _progress;
	private readonly SummaryPanel _summary;
	private readonly DataTable<TypeStat> _heap;
	private readonly DataTable<TypeDelta> _diff;
	private HeapSnapshot? _shown;

	public HeapTab(DotnetProfilerService service) {
		_service = service;
		Name = "Heap";

		_toolbar = new FlowRow { WidthOf = Width, Border = false };
		_take = new WebButton("Take Snapshot", () => _ = service.SnapshotAsync(), ButtonStyle.Primary);
		_compare = new WebButton("Compare Last Two", service.CompareSnapshots);
		_pick = new SelectBox { FontSize = 12f, Changed = index => {
			if ((uint)index < (uint)service.Snapshots.Count) Show(service.Snapshots[index], fromSelect: true);
		} };
		_progress = new Label { FontSize = 11.5f, Foreground = Theme.EditorForeground.WithAlpha(0.7f) };
		_toolbar.AddChild(_take);
		_toolbar.AddChild(_compare);
		_toolbar.AddChild(_pick);
		_toolbar.AddChild(_progress);
		_toolbar.Fill = _progress;

		_summary = new SummaryPanel(this);

		_heap = new DataTable<TypeStat>([
			new("type", "Type", 420f, static (left, right) => string.CompareOrdinal(left.TypeName, right.TypeName), static (painter, row, in cell) => DataTable<TypeStat>.Text(painter, painter.GetMono(12f), row.TypeName, cell, Theme.EditorForeground)) { DefaultDescending = false },
			new("bytes", "Size", 100f, static (left, right) => left.Bytes.CompareTo(right.Bytes), static (painter, row, in cell) => DataTable<TypeStat>.Text(painter, painter.GetUi(12f), Engine.Format.Bytes(row.Bytes), cell, Theme.EditorForeground, right: true)) { Numeric = true },
			new("count", "Objects", 100f, static (left, right) => left.Count.CompareTo(right.Count), static (painter, row, in cell) => DataTable<TypeStat>.Text(painter, painter.GetUi(12f), Engine.Format.Number(row.Count), cell, Theme.EditorForeground, right: true)) { Numeric = true },
			new("avg", "Avg Size", 90f, static (left, right) => Average(left).CompareTo(Average(right)), static (painter, row, in cell) => DataTable<TypeStat>.Text(painter, painter.GetUi(12f), Engine.Format.Bytes(Average(row)), cell, Theme.DescriptionForeground, right: true)) { Numeric = true },
			new("share", "Share", 140f, static (left, right) => left.Bytes.CompareTo(right.Bytes), static (painter, row, in cell) => DataTable<TypeStat>.Bar(painter, row.Bytes, cell, SizeBar)),
		], "bytes") { Limit = 400, ScaleBy = static row => row.Bytes };

		_diff = new DataTable<TypeDelta>([
			new("type", "Type", 420f, static (left, right) => string.CompareOrdinal(left.TypeName, right.TypeName), static (painter, row, in cell) => DataTable<TypeDelta>.Text(painter, painter.GetMono(12f), row.TypeName, cell, Theme.EditorForeground)) { DefaultDescending = false },
			new("bytes", "Δ Bytes", 100f, static (left, right) => left.ByteDelta.CompareTo(right.ByteDelta), static (painter, row, in cell) => DataTable<TypeDelta>.Text(painter, painter.GetUi(12f), Engine.Format.SignedBytes(row.ByteDelta), cell, row.ByteDelta >= 0 ? Grew : Shrank, right: true)) { Numeric = true },
			new("objects", "Δ Objects", 100f, static (left, right) => left.CountDelta.CompareTo(right.CountDelta), static (painter, row, in cell) => DataTable<TypeDelta>.Text(painter, painter.GetUi(12f), Engine.Format.Signed(row.CountDelta), cell, Theme.EditorForeground, right: true)) { Numeric = true },
			new("count", "Count", 160f, static (left, right) => left.CountAfter.CompareTo(right.CountAfter), static (painter, row, in cell) => DataTable<TypeDelta>.Text(painter, painter.GetUi(12f), $"{Engine.Format.Number(row.CountBefore)} → {Engine.Format.Number(row.CountAfter)}", cell, Theme.DescriptionForeground, right: true)) { Numeric = true },
			new("change", "Change", 140f, static (left, right) => left.ByteDelta.CompareTo(right.ByteDelta), static (painter, row, in cell) => DataTable<TypeDelta>.Bar(painter, Math.Abs(row.ByteDelta), cell, row.ByteDelta >= 0 ? Grew.WithAlpha(0.55f) : Shrank.WithAlpha(0.5f))),
		], "bytes") { ScaleBy = static row => Math.Abs(row.ByteDelta), Visible = false };

		AddChild(_toolbar);
		AddChild(_summary);
		AddChild(_heap);
		AddChild(_diff);

		service.Changed += change => {
			if ((change & (DotnetProfilerChange.Snapshots | DotnetProfilerChange.Session)) != 0) Show(service.Snapshots.Count > 0 ? service.Snapshots[^1] : null);

			if ((change & (DotnetProfilerChange.Diff | DotnetProfilerChange.Session)) != 0) ShowDiff(service.Diff);

			if ((change & (DotnetProfilerChange.Session | DotnetProfilerChange.Snapshots | DotnetProfilerChange.Status)) != 0) SyncButtons();
		};

		Show(service.Snapshots.Count > 0 ? service.Snapshots[^1] : null);
		ShowDiff(service.Diff);
		SyncButtons();
	}

	private static double Average(TypeStat stat) => stat.Count > 0 ? stat.Bytes / (double)stat.Count : 0;

	private float Width(Panel child) {
		float scale = Scale;

		return child switch {
			WebButton button => button.PreferredWidth(scale),
			SelectBox select => MathF.Max(120f * scale, select.PreferredWidth(scale)),
			_ => 100f * scale,
		};
	}

	private void SyncButtons() {
		IReadOnlyList<HeapSnapshot> snapshots = _service.Snapshots;

		_take.Enabled = _service.Live;
		_compare.Enabled = snapshots.Count >= 2;
		_pick.Visible = snapshots.Count > 0;
		_pick.SetOptions([.. snapshots.Select(static snapshot => $"{snapshot.Timestamp:T} · {Engine.Format.Bytes(snapshot.TotalBytes)}")], _shown is null ? snapshots.Count - 1 : IndexOf(_shown));
		_progress.SetText(_service.Status.StartsWith("Capturing", StringComparison.Ordinal) ? "capturing heap..." : string.Empty);
		_toolbar.InvalidateLayout();
	}

	private int IndexOf(HeapSnapshot snapshot) {
		IReadOnlyList<HeapSnapshot> snapshots = _service.Snapshots;

		for (int i = 0; i < snapshots.Count; i++) {
			if (ReferenceEquals(snapshots[i], snapshot)) return i;
		}

		return snapshots.Count - 1;
	}

	private void Show(HeapSnapshot? snapshot, bool fromSelect = false) {
		_shown = snapshot;
		_heap.SetData(snapshot?.Types ?? []);

		if (!fromSelect) SyncButtons();

		_summary.Repaint();
	}

	private void ShowDiff(SnapshotDiff? diff) {
		_diff.Visible = diff is not null;
		_diff.SetData(diff?.Changes ?? []);
		InvalidateLayout();
		_summary.Repaint();
	}

	public override void PerformLayout() {
		float scale = Scale;
		float padding = 10f * scale;
		float toolbar = 34f * scale;
		float summary = SummaryPanel.Height(scale, _service.Diff is not null);
		float top = padding + toolbar + summary;
		float rest = MathF.Max(0f, Tall - top - padding);

		_toolbar.SetBounds(0f, padding - (6f * scale), Wide, toolbar);
		_summary.SetBounds(padding, padding + toolbar, Wide - (padding * 2f), summary);

		if (_diff.Visible) {
			float diffHeight = rest * 0.45f;
			_diff.SetBounds(padding, top, Wide - (padding * 2f), diffHeight);
			_heap.SetBounds(padding, top + diffHeight + (20f * scale), Wide - (padding * 2f), rest - diffHeight - (20f * scale));
		}
		else _heap.SetBounds(padding, top, Wide - (padding * 2f), rest);
	}

	private sealed class SummaryPanel(HeapTab owner) : Panel
	{
		public static float Height(float scale, bool diff) => (diff ? 110f : 62f) * scale;

		public override void Paint(IPainter painter) {
			float scale = Scale;
			ITextFont summaryFont = painter.GetUi(13f);
			ITextFont strong = painter.GetUi(13f, strong: true);
			ITextFont chip = painter.GetUi(11.5f);
			float y = ScreenY;

			if (owner._shown is not { } snapshot) {
				painter.DrawText(summaryFont, owner._service.Live ? "Take a snapshot to see what is on the managed heap." : "Attach to a process to take heap snapshots.", ScreenX, y + summaryFont.GetBaseline(), Theme.EditorForeground.WithAlpha(0.65f));
				return;
			}

			float x = ScreenX;
			float baseline = y + summaryFont.GetBaseline();

			void Part(ITextFont font, string text, Color color) {
				painter.DrawText(font, text, x, baseline, color);
				x += font.Measure(text);
			}

			Part(strong, Engine.Format.Number(snapshot.TotalObjects), Theme.EditorForeground);
			Part(summaryFont, " objects · ", Theme.EditorForeground);
			Part(strong, Engine.Format.Bytes(snapshot.TotalBytes), Theme.EditorForeground);
			Part(summaryFont, $" · {Engine.Format.Number(snapshot.TypeCount)} distinct types", Theme.EditorForeground);

			if (snapshot.NamespaceFilter is { Length: > 0 } filter) Part(summaryFont, $" · limited to {filter}", WarnColor);

			if (snapshot.SkippedObjects > 0) Part(summaryFont, $" · {snapshot.SkippedObjects} unreadable objects skipped", WarnColor);

			y += summaryFont.GetLineHeight() + (10f * scale);
			x = ScreenX;
			double total = Math.Max(1, snapshot.TotalBytes);
			float height = 20f * scale;

			foreach (SegmentStat segment in snapshot.Segments) {
				string text = $"{segment.Kind} {Engine.Format.Bytes(segment.Bytes)} ";
				string share = $"{segment.Bytes / total * 100:0}%";
				float width = (31f * scale) + chip.Measure(text) + chip.Measure(share);
				float chipBaseline = y + ((height - chip.GetLineHeight()) / 2f) + chip.GetBaseline();

				painter.FillRoundedRectangle(x, y, width, height, 10f * scale, Theme.EditorForeground.WithAlpha(0.1f));
				painter.FillRoundedRectangle(x + (9f * scale), y + ((height - (8f * scale)) / 2f), 8f * scale, 8f * scale, 2f * scale, SegmentColors.GetValueOrDefault(segment.Kind, Frozen));
				painter.DrawText(chip, text, x + (22f * scale), chipBaseline, Theme.EditorForeground);
				painter.DrawText(chip, share, x + (22f * scale) + chip.Measure(text), chipBaseline, Theme.EditorForeground.WithAlpha(0.55f));
				x += width + (8f * scale);
			}

			if (owner._service.Diff is not { } diff) return;

			y += height + (16f * scale);
			ITextFont heading = painter.GetUi(11f, strong: true);
			painter.DrawText(heading, "CHANGE SINCE BASELINE", ScreenX, y + heading.GetBaseline(), Theme.EditorForeground.WithAlpha(0.75f));
			y += heading.GetLineHeight() + (6f * scale);

			x = ScreenX;
			baseline = y + summaryFont.GetBaseline();
			Part(strong, Engine.Format.SignedBytes(diff.ByteDelta), diff.ByteDelta >= 0 ? Grew : Shrank);
			Part(summaryFont, $" across {Engine.Format.Signed(diff.ObjectDelta)} objects", Theme.EditorForeground);
		}
	}

	private static readonly Color WarnColor = Color.Parse("#dcb45a");
	private static readonly Color Grew = Color.Parse("#e06c75");
	private static readonly Color Shrank = Color.Parse("#4ec9b0");
	private static readonly Color Frozen = Color.Parse("#888888");
	private static readonly Color SizeBar = Color.Parse("#dcb45a").WithAlpha(0.55f);
}
