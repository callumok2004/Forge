namespace Forge.Workloads;

internal static class Report
{
	public static void Print(WorkloadResult result, WorkloadResult? previous) {
		Console.WriteLine();
		Console.WriteLine($"{result.Solution}  seed={result.Options.Seed} files={result.FilesOpened} load={result.LoadSeconds:F1}s{Delta(result.LoadSeconds, previous?.LoadSeconds)} diagnostics={result.Diagnostics}");
		Console.WriteLine();
		Console.WriteLine($"{"phase",-22} {"secs",6} | {"forge cpu avg/p95/max (cores)",-30} {"forge MB avg/max",-18} {"managed MB",-11} | {"roslyn cpu avg/p95/max",-24} {"roslyn MB avg/max",-20} procs");

		foreach (PhaseResult phase in result.Phases) {
			PhaseResult? before = previous?.Phases.FirstOrDefault(candidate => candidate.Name == phase.Name);
			Console.WriteLine($"{phase.Name,-22} {phase.Seconds,6:F1} | {Cpu(phase.ForgeCpu),-30} {Memory(phase.ForgeMemoryMb, before?.ForgeMemoryMb),-18} {phase.ForgeManagedMb.Max,-11:F0} | {Cpu(phase.RoslynCpu) + Delta(phase.RoslynCpu.Mean, before?.RoslynCpu.Mean),-24} {Memory(phase.RoslynMemoryMb, before?.RoslynMemoryMb),-20} {phase.RoslynProcesses}");
		}

		Console.WriteLine();
		Console.WriteLine($"{"request",-26} {"n",5} {"mean ms",9} {"p50",8} {"p95",8} {"max",8}");

		foreach ((string name, Distribution latency) in result.Latencies.OrderBy(static pair => pair.Key, StringComparer.Ordinal)) {
			Distribution? before = previous?.Latencies.GetValueOrDefault(name);
			Console.WriteLine($"{name,-26} {latency.Count,5} {latency.Mean,9:F1} {latency.P50,8:F1} {latency.P95,8:F1} {latency.Max,8:F1}{Delta(latency.P95, before?.P95)}");
		}

		foreach (ServerProfile profile in result.Profiles) {
			Console.WriteLine();
			Console.WriteLine($"roslyn hot methods during {profile.Phase} ({profile.Samples} samples, {profile.DurationMs / 1000:F1}s)");
			Console.WriteLine($"  {"self %",7} {"incl %",7}  by self time");

			foreach (HotMethod method in profile.BySelf.Take(15)) {
				Console.WriteLine($"  {method.SelfPercent,7:F2} {method.InclusivePercent,7:F2}  {Trim(method.Name)}");

				foreach (string caller in profile.Callers.GetValueOrDefault(method.Name) ?? []) Console.WriteLine($"                     {Trim(caller)}");
			}

			Console.WriteLine($"  {"self %",7} {"incl %",7}  by inclusive time");

			foreach (HotMethod method in profile.ByInclusive.Take(15)) Console.WriteLine($"  {method.SelfPercent,7:F2} {method.InclusivePercent,7:F2}  {Trim(method.Name)}");
		}

		Console.WriteLine();
		Console.WriteLine(result.SourceChanges.Count == 0 ? "Source guard: no files outside bin/obj changed." : $"Source guard: {result.SourceChanges.Count} FILES CHANGED.");
	}

	private static string Trim(string name) => name.Length <= 260 ? name : string.Concat(name.AsSpan(0, 257), "...");

	private static string Cpu(Distribution cpu) => $"{cpu.Mean:F2}/{cpu.P95:F2}/{cpu.Max:F2}";

	private static string Memory(Distribution memory, Distribution? before) => $"{memory.Mean:F0}/{memory.Max:F0}{Delta(memory.Max, before?.Max)}";

	private static string Delta(double now, double? before) {
		if (before is not { } previous || previous == 0) return string.Empty;

		double change = (now - previous) / previous * 100;
		return Math.Abs(change) < 1 ? string.Empty : $" ({change:+0;-0}%)";
	}
}
