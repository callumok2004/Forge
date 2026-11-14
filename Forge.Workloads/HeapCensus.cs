using Microsoft.Diagnostics.Runtime;

namespace Forge.Workloads;

internal sealed record HeapEntry(string Type, long Count, long Bytes);

internal static class HeapCensus
{
	public static List<HeapEntry> Take(int top) {
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		using DataTarget target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
		using ClrRuntime runtime = target.ClrVersions[0].CreateRuntime();
		Dictionary<string, (long Count, long Bytes)> totals = [with(StringComparer.Ordinal)];

		foreach (ClrObject item in runtime.Heap.EnumerateObjects()) {
			if (!item.IsValid || item.IsFree) continue;

			string name = item.Type?.Name ?? "?";
			(long count, long bytes) = totals.GetValueOrDefault(name);
			totals[name] = (count + 1, bytes + (long)item.Size);
		}

		return [.. totals.OrderByDescending(static pair => pair.Value.Bytes).Take(top).Select(static pair => new HeapEntry(pair.Key, pair.Value.Count, pair.Value.Bytes))];
	}
}
