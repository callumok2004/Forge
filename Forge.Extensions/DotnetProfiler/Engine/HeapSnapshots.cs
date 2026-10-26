using System.Runtime.InteropServices;

using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace Forge.Extensions.DotnetProfiler.Engine;

public static class HeapSnapshots
{
	public const int TopTypes = 500;
	public const int DiffTypes = 200;

	public static HeapSnapshot Capture(DiagnosticsClient client, int pid, string snapshotId, NamespaceFilter? filter, Action<string>? warn) {
		string path = TempFiles.NewDumpPath(pid);
		client.WriteDump(DumpType.WithHeap, path, logDumpGeneration: false);

		try {
			return Analyze(path, pid, snapshotId, filter, warn);
		}
		finally {
			try {
				File.Delete(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			}
		}
	}

	private static HeapSnapshot Analyze(string dumpPath, int pid, string snapshotId, NamespaceFilter? filter, Action<string>? warn) {
		using DataTarget target = DataTarget.LoadDump(dumpPath);
		ClrInfo info = target.ClrVersions.FirstOrDefault() ?? throw new InvalidOperationException("no CLR runtime found in the dump");

		using ClrRuntime runtime = info.CreateRuntime();
		ClrHeap heap = runtime.Heap;

		if (!heap.CanWalkHeap) throw new InvalidOperationException("the heap is not walkable (dump captured mid-GC)");

		Dictionary<ClrType, (long Count, long Bytes)> byType = new(4096);
		Dictionary<GCSegmentKind, (long Bytes, long Objects)> bySegment = [];
		Dictionary<ClrType, bool> matches = [];
		long totalObjects = 0;
		long totalBytes = 0;
		long skipped = 0;

		foreach (ClrSegment segment in heap.Segments) {
			ref (long Bytes, long Objects) segmentSlot = ref CollectionsMarshal.GetValueRefOrAddDefault(bySegment, segment.Kind, out _);

			try {
				foreach (ClrObject item in segment.EnumerateObjects(carefully: true)) {
					if (item.IsFree) continue;

					if (item.Type is not { } type) {
						skipped++;
						continue;
					}

					if (filter is not null) {
						ref bool matched = ref CollectionsMarshal.GetValueRefOrAddDefault(matches, type, out bool known);

						if (!known) matched = filter.Matches(type.Name);

						if (!matched) continue;
					}

					long size = (long)item.Size;
					ref (long Count, long Bytes) slot = ref CollectionsMarshal.GetValueRefOrAddDefault(byType, type, out _);
					slot.Count++;
					slot.Bytes += size;
					segmentSlot.Bytes += size;
					segmentSlot.Objects++;
					totalObjects++;
					totalBytes += size;
				}
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException) {
				warn?.Invoke($"heap walk stopped early on {SegmentName(segment.Kind)} segment: {exception.Message}");
			}
		}

		Dictionary<string, (long Count, long Bytes)> byName = new(byType.Count, StringComparer.Ordinal);

		foreach ((ClrType type, (long count, long bytes)) in byType) {
			ref (long Count, long Bytes) slot = ref CollectionsMarshal.GetValueRefOrAddDefault(byName, type.Name ?? "<unknown>", out _);
			slot.Count += count;
			slot.Bytes += bytes;
		}

		List<TypeStat> types = new(byName.Count);

		foreach ((string name, (long count, long bytes)) in byName) types.Add(new TypeStat(name, count, bytes));

		types.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));

		if (types.Count > TopTypes) types.RemoveRange(TopTypes, types.Count - TopTypes);

		List<SegmentStat> segments = [];

		foreach ((GCSegmentKind kind, (long bytes, long objects)) in bySegment) {
			if (objects > 0) segments.Add(new SegmentStat(SegmentName(kind), bytes, objects));
		}

		segments.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));

		return new HeapSnapshot(snapshotId, pid, DateTimeOffset.Now, totalObjects, totalBytes, byName.Count, types, segments, skipped, filter?.Text);
	}

	public static string SegmentName(GCSegmentKind kind) => kind switch {
		GCSegmentKind.Generation0 => "gen0",
		GCSegmentKind.Generation1 => "gen1",
		GCSegmentKind.Generation2 => "gen2",
		GCSegmentKind.Large => "loh",
		GCSegmentKind.Pinned => "poh",
		GCSegmentKind.Frozen => "frozen",
		GCSegmentKind.Ephemeral => "ephemeral",
		_ => kind.ToString().ToLowerInvariant(),
	};

	public static SnapshotDiff Diff(HeapSnapshot baseline, HeapSnapshot current) {
		Dictionary<string, TypeStat> before = new(baseline.Types.Count, StringComparer.Ordinal);

		foreach (TypeStat stat in baseline.Types) before[stat.TypeName] = stat;

		List<TypeDelta> changes = [];

		foreach (TypeStat after in current.Types) {
			before.Remove(after.TypeName, out TypeStat? previous);

			if (previous is not null && previous.Bytes == after.Bytes && previous.Count == after.Count) continue;

			changes.Add(new TypeDelta(after.TypeName, previous?.Count ?? 0, after.Count, previous?.Bytes ?? 0, after.Bytes));
		}

		foreach (TypeStat gone in before.Values) changes.Add(new TypeDelta(gone.TypeName, gone.Count, 0, gone.Bytes, 0));

		List<TypeDelta> growth = [.. changes.Where(static delta => delta.ByteDelta > 0).OrderByDescending(static delta => delta.ByteDelta).Take(DiffTypes)];
		List<TypeDelta> shrink = [.. changes.Where(static delta => delta.ByteDelta < 0).OrderBy(static delta => delta.ByteDelta).Take(DiffTypes)];

		return new SnapshotDiff(baseline.SnapshotId, current.SnapshotId, current.TotalObjects - baseline.TotalObjects, current.TotalBytes - baseline.TotalBytes, [.. growth, .. shrink]);
	}
}
