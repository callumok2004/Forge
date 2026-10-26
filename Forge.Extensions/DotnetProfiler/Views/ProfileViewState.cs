using System.Globalization;

using Forge.Extensions.DotnetProfiler.Engine;

namespace Forge.Extensions.DotnetProfiler.Views;

public enum Metric
{
	Cpu,
	Alloc,
}

public enum Unit
{
	Milliseconds,
	PerSecond,
	PerFrame,
	Raw,
}

public sealed record Baseline(FunctionIndex? Cpu, FunctionIndex? Alloc, double DurationSeconds, string Label);

public sealed class ProfileViewState
{
	public const double SampleMilliseconds = 1;
	public const int LowConfidence = 20;

	private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

	public CpuProfile? Profile { get; private set; }

	public FunctionIndex? Cpu { get; private set; }

	public FunctionIndex? Alloc { get; private set; }

	public double DurationSeconds { get; private set; }

	public Unit Unit { get; set; } = Unit.Milliseconds;

	public int Fps { get; set; } = 60;

	public bool Grouped { get; private set; } = true;

	public bool FullSignatures { get; set; }

	public Baseline? Baseline { get; set; }

	public event Action? Changed;

	public FunctionIndex? Index(Metric metric) => metric == Metric.Cpu ? Cpu : Alloc;

	public ProfileTree Tree(Metric metric) => (metric == Metric.Cpu ? Profile?.Cpu : Profile?.Allocations) ?? ProfileTree.Empty;

	public void Load(CpuProfile? profile) {
		Profile = profile;
		DurationSeconds = (profile?.DurationMs ?? 0) / 1000.0;
		Rebuild();
	}

	public void SetGrouped(bool grouped) {
		if (Grouped == grouped) return;

		Grouped = grouped;
		Baseline = null;
		Rebuild();
	}

	private void Rebuild() {
		Cpu = Profile is { Cpu.Count: > 1 } cpu ? new FunctionIndex(cpu.Cpu, Grouped) : null;
		Alloc = Profile is { Allocations.Count: > 1 } alloc ? new FunctionIndex(alloc.Allocations, Grouped) : null;
		Changed?.Invoke();
	}

	public void Notify() => Changed?.Invoke();

	public double Convert(Metric metric, double raw, double? duration = null) {
		double seconds = duration ?? DurationSeconds;
		double value = metric == Metric.Cpu && Unit != Unit.Raw ? raw * SampleMilliseconds : raw;

		return Unit switch {
			Unit.PerSecond => seconds > 0 ? value / seconds : 0,
			Unit.PerFrame => seconds > 0 ? value / seconds / Fps : 0,
			_ => value,
		};
	}

	public string FormatValue(Metric metric, double value) {
		if (metric == Metric.Alloc) return Engine.Format.Bytes(value) + (Unit == Unit.PerSecond ? "/s" : Unit == Unit.PerFrame ? "/frame" : string.Empty);

		return Unit switch {
			Unit.Raw => Math.Round(value).ToString("N0", Invariant),
			Unit.PerSecond => value.ToString(value < 10 ? "0.00" : "0.0", Invariant) + " ms/s",
			Unit.PerFrame => value < 0.01 ? (value * 1000).ToString("0.0", Invariant) + " µs" : value.ToString("0.000", Invariant) + " ms",
			_ => value >= 1000 ? (value / 1000).ToString("0.00", Invariant) + " s" : value.ToString(value < 10 ? "0.0" : "0", Invariant) + " ms",
		};
	}

	public string Format(Metric metric, double raw) => FormatValue(metric, Convert(metric, raw));

	public string Percent(Metric metric, double raw) {
		double total = Index(metric)?.RootTotal ?? 0;

		return (total > 0 ? raw * 100 / total : 0).ToString("0.00", Invariant) + "%";
	}

	public string UnitHint(Metric metric) => Unit switch {
		Unit.PerFrame => $"per frame @ {Fps} fps",
		Unit.PerSecond => "per second of recording",
		Unit.Raw => metric == Metric.Cpu ? "raw samples" : "bytes",
		_ => metric == Metric.Cpu ? "CPU time (1 sample ≈ 1 ms of one thread)" : "bytes",
	};

	public double? BaselineDelta(Metric metric, FunctionRow row) {
		if (Baseline is not { } baseline || (metric == Metric.Cpu ? baseline.Cpu : baseline.Alloc) is not { } index) return null;

		double previous = index.Get(row.Key)?.Own ?? 0;
		double scaled = baseline.DurationSeconds > 0 ? previous * (DurationSeconds / baseline.DurationSeconds) : previous;
		return row.Own - scaled;
	}

	public long AllocOwn(FunctionRow row) => Alloc?.Get(row.Key)?.Own ?? 0;
}
