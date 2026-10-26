using System.Collections.Frozen;

namespace Forge.Extensions.DotnetProfiler.Engine;

public enum Counter
{
	CpuUsage,
	GcHeapSize,
	WorkingSet,
	AllocRate,
	ThreadCount,
	TimeInGc,
	GcFragmentation,
	ThreadPoolQueueLength,
	ThreadPoolThreadCount,
	MethodsJitted,
	LockContention,
	GcPauseCount,
	Gen0Count,
	Gen1Count,
	Gen2Count,
	HandleCount,
	PrivateBytes,
	Exceptions,
}

public static class Counters
{
	public const int Count = (int)Counter.Exceptions + 1;

	public static FrozenDictionary<string, Counter> ByName { get; } = new Dictionary<string, Counter>(StringComparer.Ordinal) {
		["cpu-usage"] = Counter.CpuUsage,
		["gc-heap-size"] = Counter.GcHeapSize,
		["working-set"] = Counter.WorkingSet,
		["alloc-rate"] = Counter.AllocRate,
		["time-in-gc"] = Counter.TimeInGc,
		["gc-fragmentation"] = Counter.GcFragmentation,
		["threadpool-queue-length"] = Counter.ThreadPoolQueueLength,
		["threadpool-thread-count"] = Counter.ThreadPoolThreadCount,
		["methods-jitted-count"] = Counter.MethodsJitted,
		["monitor-lock-contention-count"] = Counter.LockContention,
		["gen-0-gc-count"] = Counter.Gen0Count,
		["gen-1-gc-count"] = Counter.Gen1Count,
		["gen-2-gc-count"] = Counter.Gen2Count,
	}.ToFrozenDictionary(StringComparer.Ordinal);

	public static double Scale(Counter counter) => counter is Counter.GcHeapSize or Counter.WorkingSet ? 1e6 : 1;

	public static bool IsCumulative(Counter counter) => counter is Counter.LockContention or Counter.GcPauseCount;
}

public sealed class CounterHistory
{
	public const int Capacity = 240;

	private readonly double[] _values = new double[Capacity];
	private readonly byte[] _marks = new byte[Capacity];
	private int _head;
	private byte _pendingMark;

	public int Count { get; private set; }

	public double Latest { get; private set; } = double.NaN;

	public int Version { get; private set; }

	public void Push(double value) {
		if (!double.IsFinite(value)) return;

		_values[_head] = value;
		_marks[_head] = _pendingMark;
		_pendingMark = 0;
		_head = (_head + 1) % Capacity;
		Latest = value;

		if (Count < Capacity) Count++;

		Version++;
	}

	public void Mark(int generation) {
		byte level = (byte)(Math.Clamp(generation, 0, 2) + 1);

		if (level > _pendingMark) _pendingMark = level;
	}

	public double At(int index) => _values[(Count == Capacity ? _head + index : index) % Capacity];

	public byte MarkAt(int index) => _marks[(Count == Capacity ? _head + index : index) % Capacity];

	public void Clear() {
		Array.Clear(_values);
		Array.Clear(_marks);
		_head = 0;
		_pendingMark = 0;
		Count = 0;
		Latest = double.NaN;
		Version++;
	}
}
