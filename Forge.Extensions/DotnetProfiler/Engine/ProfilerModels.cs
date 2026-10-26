namespace Forge.Extensions.DotnetProfiler.Engine;

public sealed record ProcessInfo(int Pid, string Name, string? CommandLine);

public sealed record AllocStack(IReadOnlyList<string> Frames, long Bytes, long Count);

public sealed record AllocSite(string TypeName, long Bytes, long Count, string? TopFrame, IReadOnlyList<AllocStack>? Stacks);

public sealed record CpuProfile(long TotalSamples, double DurationMs, ProfileTree Cpu, ProfileTree Allocations, IReadOnlyList<AllocSite> Sites, long TotalAllocBytes, string? NamespaceFilter);

public sealed record TypeStat(string TypeName, long Count, long Bytes);

public sealed record SegmentStat(string Kind, long Bytes, long Objects);

public sealed record HeapSnapshot(string SnapshotId, int Pid, DateTimeOffset Timestamp, long TotalObjects, long TotalBytes, int TypeCount, IReadOnlyList<TypeStat> Types, IReadOnlyList<SegmentStat> Segments, long SkippedObjects, string? NamespaceFilter);

public sealed record TypeDelta(string TypeName, long CountBefore, long CountAfter, long BytesBefore, long BytesAfter)
{
	public long ByteDelta => BytesAfter - BytesBefore;

	public long CountDelta => CountAfter - CountBefore;
}

public sealed record SnapshotDiff(string BaselineId, string CurrentId, long ObjectDelta, long ByteDelta, IReadOnlyList<TypeDelta> Changes);

public readonly record struct GcHeapStats(long Gen0, long Gen1, long Gen2, long Loh, long Poh);

public enum TempCleanupScope
{
	All,
	Stale,
}

public readonly record struct TempSurvey(int Count, long Bytes, string Directory);

public readonly record struct TempCleanup(int Deleted, long Bytes, int Locked, string Directory);
