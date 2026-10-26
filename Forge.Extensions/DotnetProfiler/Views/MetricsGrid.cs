using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public readonly record struct MetricSpec(Counter Counter, string Label, Color Color, Func<double, string> Format, bool Markers = false);

public sealed class MetricsGrid : Panel
{
	public static readonly MetricSpec[] Full = [
		new(Counter.CpuUsage, "CPU %", Color.Parse("#569cd6"), Engine.Format.Percent),
		new(Counter.GcHeapSize, "Managed Heap", Color.Parse("#4ec9b0"), Engine.Format.Bytes, true),
		new(Counter.WorkingSet, "Working Set", Color.Parse("#78aaff"), Engine.Format.Bytes, true),
		new(Counter.AllocRate, "Alloc Rate", Color.Parse("#dcb45a"), Engine.Format.Rate, true),
		new(Counter.ThreadCount, "Threads (OS)", Color.Parse("#c586c0"), Engine.Format.Count),
		new(Counter.Exceptions, "Exceptions/s", Color.Parse("#e06c75"), Engine.Format.PerSecond),
		new(Counter.TimeInGc, "Time in GC %", Color.Parse("#ff8c5a"), Engine.Format.Percent),
		new(Counter.GcFragmentation, "Heap Fragmentation", Color.Parse("#c878c8"), Engine.Format.Percent),
		new(Counter.LockContention, "Lock Contention/s", Color.Parse("#f0c864"), Engine.Format.PerSecond),
		new(Counter.ThreadPoolQueueLength, "Threadpool Queue", Color.Parse("#82c8a0"), Engine.Format.Count),
		new(Counter.GcPauseCount, "GC Pauses", Color.Parse("#b4b4b4"), Engine.Format.PerSecond),
		new(Counter.MethodsJitted, "Methods Jitted", Color.Parse("#96beff"), Engine.Format.Count),
	];

	public static readonly MetricSpec[] Compact = [.. Full.Where(static spec => spec.Counter is Counter.CpuUsage or Counter.GcHeapSize or Counter.WorkingSet or Counter.AllocRate or Counter.ThreadCount or Counter.TimeInGc)];

	private readonly Sparkline[] _charts;
	private readonly ChartVariant _variant;

	public MetricsGrid(DotnetProfilerService service, MetricSpec[] specs, ChartVariant variant) {
		_variant = variant;
		Name = "MetricsGrid";
		MouseInputEnabled = false;
		_charts = new Sparkline[specs.Length];

		for (int i = 0; i < specs.Length; i++) {
			MetricSpec spec = specs[i];
			_charts[i] = new Sparkline(service.History(spec.Counter), spec.Label, spec.Color, spec.Format, spec.Markers, variant);
			AddChild(_charts[i]);
		}
	}

	public void Sync() {
		foreach (Sparkline chart in _charts) chart.Sync();
	}

	private float Gap => (_variant == ChartVariant.Mini ? 8f : 10f) * Scale;

	private int Columns(float width) {
		float css = width / Scale;

		return _variant switch {
			ChartVariant.Mini => 1,
			ChartVariant.Panel => css <= 560f ? 1 : css <= 900f ? 2 : 3,
			_ => css <= 520f ? 1 : css <= 860f ? 2 : css <= 1180f ? 3 : 4,
		};
	}

	public float ContentHeight(float width) {
		int columns = Columns(width);
		int rows = (_charts.Length + columns - 1) / columns;

		return (rows * Sparkline.PreferredHeight(Scale, _variant)) + (Math.Max(0, rows - 1) * Gap);
	}

	public override void PerformLayout() {
		float gap = Gap;
		int columns = Columns(Wide);
		float width = (Wide - (gap * (columns - 1))) / columns;
		float height = Sparkline.PreferredHeight(Scale, _variant);

		for (int i = 0; i < _charts.Length; i++) _charts[i].SetBounds((i % columns) * (width + gap), (i / columns) * (height + gap), width, height);
	}
}

public sealed class GenerationStrip : Panel
{
	private static readonly Color[] PipColors = [Color.Parse("#4ec9b0"), Color.Parse("#dcb45a"), Color.Parse("#e06c75")];
	private static readonly float[] PipHeights = [8f, 16f, 26f];

	private readonly DotnetProfilerService _service;

	public GenerationStrip(DotnetProfilerService service) {
		_service = service;
		Name = "Generations";
		MouseInputEnabled = false;
	}

	private const float ChipHeight = 20f;
	private const float ChipGap = 6f;

	public float ContentHeight(float width) {
		float scale = Scale;
		int rows = Ui is null ? 1 : Layout(Ui.GetFonts().GetUi(11f), Ui.GetFonts().GetUi(11f, strong: true), width, scale, null);

		return ((12f + (rows * ChipHeight) + ((rows - 1) * ChipGap) + 14f + 11f * 1.4f + 6f + 26f) * scale);
	}

	private IEnumerable<(string Label, string Value)> Chips() {
		GcHeapStats? stats = _service.GcStats;

		yield return ("gen0 ", stats is { } a ? Engine.Format.Bytes(a.Gen0) : "–");
		yield return ("gen1 ", stats is { } b ? Engine.Format.Bytes(b.Gen1) : "–");
		yield return ("gen2 ", stats is { } c ? Engine.Format.Bytes(c.Gen2) : "–");
		yield return ("LOH ", stats is { } d ? Engine.Format.Bytes(d.Loh) : "–");
		yield return ("POH ", stats is { } e ? Engine.Format.Bytes(e.Poh) : "–");
		yield return ("collections g0/g1/g2 ", $"{Value(Counter.Gen0Count)}/{Value(Counter.Gen1Count)}/{Value(Counter.Gen2Count)}");
		yield return ("pool threads ", Value(Counter.ThreadPoolThreadCount));
		yield return ("pool queue ", Value(Counter.ThreadPoolQueueLength));
		yield return ("handles ", Value(Counter.HandleCount));
	}

	private string Value(Counter counter) => _service.Readout(counter) is var value && double.IsNaN(value) ? "–" : Engine.Format.Count(value);

	private int Layout(ITextFont font, ITextFont strong, float width, float scale, IPainter? painter) {
		float x = 0f;
		float y = 12f * scale;
		int rows = 1;
		float height = ChipHeight * scale;

		foreach ((string label, string value) in Chips()) {
			float chipWidth = font.Measure(label) + strong.Measure(value) + (18f * scale);

			if (x > 0f && x + chipWidth > width) {
				x = 0f;
				y += height + (ChipGap * scale);
				rows++;
			}

			if (painter is not null) {
				float baseline = ScreenY + y + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
				painter.FillRoundedRectangle(ScreenX + x, ScreenY + y, chipWidth, height, 10f * scale, Theme.EditorForeground.WithAlpha(0.12f));
				painter.DrawText(font, label, ScreenX + x + (9f * scale), baseline, Theme.EditorForeground.WithAlpha(0.85f));
				painter.DrawText(strong, value, ScreenX + x + (9f * scale) + font.Measure(label), baseline, Theme.EditorForeground);
			}

			x += chipWidth + (ChipGap * scale);
		}

		return rows;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi(11f);
		ITextFont strong = painter.GetUi(11f, strong: true);
		int rows = Layout(font, strong, Wide, scale, painter);
		float y = ScreenY + ((12f + (rows * ChipHeight) + ((rows - 1) * ChipGap) + 14f) * scale);

		painter.DrawText(strong, "GC EVENTS", ScreenX, y + strong.GetBaseline(), Theme.EditorForeground.WithAlpha(0.75f));
		y += strong.GetLineHeight() + (6f * scale);

		float bottom = y + (26f * scale);
		float x = ScreenX;

		foreach (int generation in _service.GcPips) {
			if (x + (4f * scale) > ScreenX + Wide) break;

			int index = Math.Clamp(generation, 0, 2);
			float height = PipHeights[index] * scale;
			painter.FillRoundedRectangle(x, bottom - height, 4f * scale, height, scale, PipColors[index]);
			x += 6f * scale;
		}
	}
}
