using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Forge.Extensions.DotnetProfiler.Engine;

public interface ISessionSink
{
	void OnCounters(ProfilingSession session, ReadOnlySpan<double> values);

	void OnGcStats(ProfilingSession session, GcHeapStats stats);

	void OnGcEvent(ProfilingSession session, int generation, int count);

	void OnException(ProfilingSession session, string type, string message);

	void OnLog(string message);

	void OnCountersEnded(ProfilingSession session);
}

public sealed class ProfilingSession : IDisposable
{
	private const long GcKeyword = 0x1;
	private const long ContentionKeyword = 0x4000;
	private const long ExceptionKeyword = 0x8000;
	private const long RuntimeKeywords = 0x14C14FCCBD;
	private const int StackDepth = 512;
	private const int DetailedTypes = 50;
	private const int StacksPerType = 5;
	private const int MaximumSites = 200;
	private const long ProcessSampleTicks = 1000;

	private readonly ISessionSink _sink;
	private readonly DiagnosticsClient _client;
	private readonly Lock _gate = new();
	private readonly List<HeapSnapshot> _snapshots = [];

	private EventPipeSession? _counterSession;
	private Thread? _counterThread;
	private EventPipeSession? _cpuSession;
	private TaskCompletionSource<CpuProfile>? _cpuDone;
	private long _cpuStarted;
	private long _pauseCount;
	private int _intervalMs = 250;
	private NamespaceFilter? _filter;
	private int _nextSnapshot;
	private bool _restarting;

	public ProfilingSession(string id, int pid, string name, ISessionSink sink, DiagnosticsClient? client = null) {
		Id = id;
		Pid = pid;
		Name = name;
		_sink = sink;
		_client = client ?? new DiagnosticsClient(pid);
	}

	public string Id { get; }

	public int Pid { get; }

	public string Name { get; }

	public bool Recording {
		get {
			lock (_gate) return _cpuSession is not null;
		}
	}

	public IReadOnlyList<HeapSnapshot> Snapshots {
		get {
			lock (_gate) return [.. _snapshots];
		}
	}

	private static EventPipeProvider[] CounterProviders(int intervalMs) {
		Dictionary<string, string> arguments = new() { ["EventCounterIntervalSec"] = (intervalMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) };

		return [
			new("System.Runtime", EventLevel.Informational, 0, arguments),
			new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, GcKeyword | ExceptionKeyword | ContentionKeyword),
		];
	}

	private static EventPipeProvider[] CpuProviders(bool allocations) => [
		new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
		new("Microsoft-Windows-DotNETRuntime", allocations ? EventLevel.Verbose : EventLevel.Informational, RuntimeKeywords),
	];

	public void StartCounters(int intervalMs) {
		intervalMs = Math.Clamp(intervalMs, 50, 10_000);

		lock (_gate) {
			if (_counterSession is not null) return;

			_intervalMs = intervalMs;
			_counterSession = _client.StartEventPipeSession(CounterProviders(intervalMs), requestRundown: false, circularBufferMB: 16);
		}

		_counterThread = new Thread(CounterLoop) { IsBackground = true, Name = $"profiler-counters-{Id}", Priority = ThreadPriority.BelowNormal };
		_counterThread.Start();
	}

	public void SetInterval(int intervalMs) {
		EventPipeSession? previous;

		lock (_gate) {
			previous = _counterSession;
			_counterSession = null;
			_restarting = previous is not null;
		}

		if (previous is null) return;

		try {
			Stop(previous);
			_counterThread?.Join(5000);
			previous.Dispose();
			StartCounters(intervalMs);
		}
		finally {
			lock (_gate) _restarting = false;
		}
	}

	public string? SetFilter(string? text) {
		NamespaceFilter? filter = NamespaceFilter.Parse(text);

		lock (_gate) _filter = filter;

		return filter?.Text;
	}

	private void CounterLoop() {
		EventPipeSession? session;

		lock (_gate) session = _counterSession;

		if (session is null) return;

		double[] pending = new double[Counters.Count];
		Array.Fill(pending, double.NaN);
		long lastFlush = Stopwatch.GetTimestamp();
		long lastProcessSample = 0;
		Process? process = null;

		try {
			using EventPipeEventSource source = new(session.EventStream);

			source.Clr.GCHeapStats += data => _sink.OnGcStats(this, new GcHeapStats(data.GenerationSize0, data.GenerationSize1, data.GenerationSize2, data.GenerationSize3, data.GenerationSize4));
			source.Clr.GCStop += data => _sink.OnGcEvent(this, data.Depth, data.Count);
			source.Clr.GCSuspendEEStop += _ => Interlocked.Increment(ref _pauseCount);
			source.Clr.ExceptionStart += data => _sink.OnException(this, data.ExceptionType, data.ExceptionMessage);

			source.Dynamic.All += data => {
				if (data.EventName != "EventCounters" || data.PayloadValue(0) is not IDictionary<string, object> outer || outer["Payload"] is not IDictionary<string, object> fields) return;

				if (fields["Name"] is not string name || !Counters.ByName.TryGetValue(name, out Counter counter)) return;

				pending[(int)counter] = Convert.ToDouble(fields["CounterType"] is "Sum" ? fields["Increment"] : fields["Mean"], CultureInfo.InvariantCulture);

				long now = Stopwatch.GetTimestamp();

				if (Stopwatch.GetElapsedTime(lastFlush, now).TotalMilliseconds < _intervalMs * 0.8) return;

				lastFlush = now;

				if (Environment.TickCount64 - lastProcessSample >= ProcessSampleTicks) {
					lastProcessSample = Environment.TickCount64;
					SampleProcess(ref process, pending);
				}

				pending[(int)Counter.GcPauseCount] = Interlocked.Read(ref _pauseCount);
				_sink.OnCounters(this, pending);
			};

			source.Process();
		}
		catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or EndOfStreamException or IOException or FormatException) {
		}
		finally {
			process?.Dispose();

			bool replaced;

			lock (_gate) replaced = _restarting || (_counterSession is not null && !ReferenceEquals(_counterSession, session));

			if (!replaced && !_disposed) _sink.OnCountersEnded(this);
		}
	}

	private void SampleProcess(ref Process? process, double[] pending) {
		try {
			process ??= Process.GetProcessById(Pid);
			process.Refresh();
			pending[(int)Counter.ThreadCount] = process.Threads.Count;
			pending[(int)Counter.PrivateBytes] = process.PrivateMemorySize64;
			pending[(int)Counter.HandleCount] = process.HandleCount;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {
			process?.Dispose();
			process = null;
		}
	}

	public void StartCpu(bool allocations, bool includeBlocked) {
		EventPipeSession session;
		TaskCompletionSource<CpuProfile> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
		NamespaceFilter? filter;

		lock (_gate) {
			if (_cpuSession is not null) return;

			_cpuStarted = Stopwatch.GetTimestamp();
			_cpuDone = done;
			filter = _filter;
			_cpuSession = session = _client.StartEventPipeSession(CpuProviders(allocations), requestRundown: false, circularBufferMB: 256);
		}

		Thread thread = new(() => CpuLoop(session, done, filter, includeBlocked), 8 * 1024 * 1024) { IsBackground = true, Name = $"profiler-cpu-{Id}" };
		thread.Start();
	}

	private static int Capture(TraceCallStack? stack, CallTree tree, int[] buffer, NamespaceFilter? filter) {
		int depth = 0;
		int keep = 0;

		for (TraceCallStack? frame = stack; frame is not null && depth < buffer.Length; frame = frame.Caller) {
			TraceCodeAddress address = frame.CodeAddress;
			string method = address.FullMethodName;

			if (string.IsNullOrEmpty(method)) method = $"0x{address.Address:x}";

			if (filter is not null && filter.Matches(method)) keep = depth + 1;

			buffer[depth++] = tree.Frames.Intern(address.ModuleName ?? string.Empty, method);
		}

		return filter is null ? depth : keep;
	}

	private void CpuLoop(EventPipeSession session, TaskCompletionSource<CpuProfile> done, NamespaceFilter? filter, bool includeBlocked) {
		CallTree cpu = new();
		CallTree allocations = new();
		Dictionary<string, int> typeIds = new(StringComparer.Ordinal);
		List<string> typeNames = [];
		Dictionary<long, (long Bytes, long Count)> sites = [];
		Dictionary<int, (long Bytes, long Count)> byType = [];
		int[] stack = new int[StackDepth];

		try {
			using TraceLogEventSource source = TraceLog.CreateFromEventPipeSession(session, TraceLog.EventPipeRundownConfiguration.Enable(_client));

			SampleProfilerTraceEventParser sampler = new(source);

			sampler.ThreadSample += data => {
				if (!includeBlocked && data.Type != ClrThreadSampleType.Managed) return;

				int depth = Capture(data.CallStack(), cpu, stack, filter);

				if (depth > 0) cpu.AddSample(stack.AsSpan(0, depth));
			};

			source.Clr.GCAllocationTick += data => {
				string type = data.TypeName;

				if (string.IsNullOrEmpty(type)) return;

				long amount = data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.AllocationAmount;
				int depth = Capture(data.CallStack(), allocations, stack, filter);

				if (depth == 0 && filter is not null) return;

				ref int typeId = ref CollectionsMarshal.GetValueRefOrAddDefault(typeIds, type, out bool known);

				if (!known) {
					typeId = typeNames.Count;
					typeNames.Add(type);
				}

				ref (long Bytes, long Count) total = ref CollectionsMarshal.GetValueRefOrAddDefault(byType, typeId, out _);
				total.Bytes += amount;
				total.Count++;

				if (depth == 0) return;

				int leaf = allocations.AddSample(stack.AsSpan(0, depth), amount);
				ref (long Bytes, long Count) site = ref CollectionsMarshal.GetValueRefOrAddDefault(sites, ((long)typeId << 32) | (uint)leaf, out _);
				site.Bytes += amount;
				site.Count++;
			};

			source.Process();
		}
		catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or EndOfStreamException or IOException or FormatException) {
		}
		catch (Exception exception) {
			_sink.OnLog($"cpu loop: {exception.Message}");
		}
		finally {
			done.TrySetResult(BuildProfile(cpu, allocations, typeNames, byType, sites, filter));
		}
	}

	private CpuProfile BuildProfile(CallTree cpu, CallTree allocations, List<string> typeNames, Dictionary<int, (long Bytes, long Count)> byType, Dictionary<long, (long Bytes, long Count)> sites, NamespaceFilter? filter) {
		ProfileTree cpuTree = cpu.Snapshot();
		ProfileTree allocTree = allocations.Snapshot();

		List<(int Type, long Bytes, long Count)> totals = new(byType.Count);

		foreach ((int type, (long bytes, long count)) in byType) totals.Add((type, bytes, count));

		totals.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));

		if (totals.Count > MaximumSites) totals.RemoveRange(MaximumSites, totals.Count - MaximumSites);

		Dictionary<int, List<(int Leaf, long Bytes, long Count)>> leaves = [];

		foreach ((long key, (long bytes, long count)) in sites) {
			int type = (int)(key >> 32);

			if (!leaves.TryGetValue(type, out List<(int, long, long)>? list)) leaves[type] = list = [];

			list.Add(((int)(uint)key, bytes, count));
		}

		List<AllocSite> result = new(totals.Count);

		for (int i = 0; i < totals.Count; i++) {
			(int type, long bytes, long count) = totals[i];
			List<AllocStack>? stacks = null;

			if (i < DetailedTypes && leaves.TryGetValue(type, out List<(int Leaf, long Bytes, long Count)>? typeLeaves)) {
				typeLeaves.Sort(static (left, right) => right.Bytes.CompareTo(left.Bytes));
				stacks = new List<AllocStack>(Math.Min(StacksPerType, typeLeaves.Count));

				for (int s = 0; s < stacks.Capacity; s++) stacks.Add(new AllocStack(allocTree.StackOf(typeLeaves[s].Leaf), typeLeaves[s].Bytes, typeLeaves[s].Count));
			}

			result.Add(new AllocSite(typeNames[type], bytes, count, stacks is { Count: > 0 } ? AttributedFrame(stacks[0].Frames) : null, stacks));
		}

		return new CpuProfile(cpuTree.RootTotal, Stopwatch.GetElapsedTime(_cpuStarted).TotalMilliseconds, cpuTree, allocTree, result, allocTree.RootTotal, filter?.Text);
	}

	public static string? AttributedFrame(IReadOnlyList<string> stack) {
		foreach (string frame in stack) {
			if (!FrameNames.IsSystem(frame, string.Empty)) return frame;
		}

		return stack.Count > 0 ? stack[0] : null;
	}

	public async Task<CpuProfile> StopCpuAsync(CancellationToken cancellation) {
		EventPipeSession? session;
		Task<CpuProfile>? done;

		lock (_gate) {
			session = _cpuSession;
			done = _cpuDone?.Task;
			_cpuSession = null;
			_cpuDone = null;
		}

		if (session is null || done is null) throw new InvalidOperationException("CPU profiling is not running");

		Stop(session);

		try {
			return await done.WaitAsync(TimeSpan.FromSeconds(60), cancellation).ConfigureAwait(false);
		}
		finally {
			session.Dispose();
		}
	}

	public async Task<HeapSnapshot> SnapshotAsync(CancellationToken cancellation) {
		string id = $"{Id}-snap{Interlocked.Increment(ref _nextSnapshot)}";
		NamespaceFilter? filter;

		lock (_gate) filter = _filter;

		HeapSnapshot snapshot = await Task.Run(() => HeapSnapshots.Capture(_client, Pid, id, filter, _sink.OnLog), cancellation).ConfigureAwait(false);

		lock (_gate) {
			_snapshots.Add(snapshot);

			if (_snapshots.Count > 10) _snapshots.RemoveAt(0);
		}

		return snapshot;
	}

	public SnapshotDiff CompareLastTwo() {
		lock (_gate) {
			if (_snapshots.Count < 2) throw new InvalidOperationException("need at least two snapshots to compare");

			return HeapSnapshots.Diff(_snapshots[^2], _snapshots[^1]);
		}
	}

	public static async Task<ProfilingSession> ListenSuspendedAsync(string id, string name, string transport, ISessionSink sink, string? filter, int intervalMs, bool allocations, bool includeBlocked, CancellationToken cancellation) {
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		timeout.CancelAfter(TimeSpan.FromSeconds(60));

		DiagnosticsClientConnector connector = await DiagnosticsClientConnector.FromDiagnosticPort($"{transport},listen", timeout.Token).ConfigureAwait(false);
		DiagnosticsClient client = connector.Instance;
		ProfilingSession session = new(id, 0, name, sink, client);

		try {
			session.SetFilter(filter);
			session.StartCounters(intervalMs);
			session.StartCpu(allocations, includeBlocked);
		}
		finally {
			client.ResumeRuntime();
		}

		return session;
	}

	private static void Stop(EventPipeSession session) {
		try {
			session.Stop();
		}
		catch (Exception exception) when (exception is ServerNotAvailableException or IOException or InvalidOperationException or TimeoutException or ObjectDisposedException) {
		}
	}

	private volatile bool _disposed;

	public void Dispose() {
		_disposed = true;
		EventPipeSession? cpu;
		EventPipeSession? counters;

		lock (_gate) {
			cpu = _cpuSession;
			counters = _counterSession;
			_cpuSession = null;
			_counterSession = null;
		}

		if (cpu is not null) {
			Stop(cpu);
			cpu.Dispose();
		}

		if (counters is not null) {
			Stop(counters);
			counters.Dispose();
		}
	}
}
