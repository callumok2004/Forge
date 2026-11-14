namespace Forge.Workloads;

internal readonly record struct Distribution(int Count, double Mean, double P50, double P95, double Max)
{
	public static Distribution Of(IEnumerable<double> values) {
		double[] sorted = [.. values.Order()];

		if (sorted.Length == 0) return default;

		return new Distribution(sorted.Length, sorted.Average(), Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted[^1]);
	}

	private static double Percentile(double[] sorted, double fraction) {
		double rank = fraction * (sorted.Length - 1);
		int low = (int)rank;
		int high = Math.Min(low + 1, sorted.Length - 1);
		return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
	}
}

internal sealed record PhaseResult(string Name, double Seconds, Distribution ForgeCpu, Distribution RoslynCpu, Distribution ForgeMemoryMb, Distribution ForgeManagedMb, Distribution RoslynMemoryMb, int RoslynProcesses);

internal sealed class Latencies
{
	private readonly Dictionary<string, List<double>> _values = [];
	private readonly Lock _sync = new();

	public async Task<T> Time<T>(string name, Func<Task<T>> operation) {
		long start = System.Diagnostics.Stopwatch.GetTimestamp();
		T result = await operation().ConfigureAwait(false);
		double milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

		lock (_sync) {
			if (!_values.TryGetValue(name, out List<double>? list)) _values[name] = list = [];

			list.Add(milliseconds);
		}

		return result;
	}

	public void Record(string name, double milliseconds) {
		lock (_sync) {
			if (!_values.TryGetValue(name, out List<double>? list)) _values[name] = list = [];

			list.Add(milliseconds);
		}
	}

	public Dictionary<string, Distribution> Summarise() {
		lock (_sync) return _values.ToDictionary(pair => pair.Key, pair => Distribution.Of(pair.Value));
	}
}

internal static class Phases
{
	public static List<PhaseResult> Summarise(IReadOnlyList<Sample> samples) {
		List<PhaseResult> results = [];

		foreach (IGrouping<string, Sample> phase in samples.GroupBy(static sample => sample.Phase)) {
			Sample[] items = [.. phase];
			double seconds = items.Length > 1 ? items[^1].Seconds - items[0].Seconds : 0;
			const double Mb = 1024.0 * 1024.0;

			results.Add(new PhaseResult(phase.Key, seconds,
				Distribution.Of(items.Select(static s => s.ForgeCpu)),
				Distribution.Of(items.Select(static s => s.RoslynCpu)),
				Distribution.Of(items.Select(static s => s.ForgeMemory / Mb)),
				Distribution.Of(items.Select(static s => s.ForgeManaged / Mb)),
				Distribution.Of(items.Select(static s => s.RoslynMemory / Mb)),
				items.Max(static s => s.RoslynProcesses)));
		}

		return results;
	}
}
