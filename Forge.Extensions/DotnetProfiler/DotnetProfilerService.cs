using System.Text.Json.Nodes;

using Forge.Common.Commands;
using Forge.Common.Debug;
using Forge.Common.UI;
using Forge.Core;
using Forge.Extensions.DotnetProfiler.Engine;

using Microsoft.Diagnostics.NETCore.Client;

namespace Forge.Extensions.DotnetProfiler;

[Flags]
public enum DotnetProfilerChange
{
	None = 0,
	Session = 1 << 0,
	Counters = 1 << 1,
	Recording = 1 << 2,
	Profile = 1 << 3,
	Snapshots = 1 << 4,
	Diff = 1 << 5,
	Processes = 1 << 6,
	Options = 1 << 7,
	Status = 1 << 8,
}

public sealed class DotnetProfilerService : ISessionSink, IDisposable
{
	public const string EditorId = "dotnetProfiler.panel";
	public const int GcPipCapacity = 120;

	private readonly Lock _gate = new();
	private readonly Dictionary<string, ProfilingSession> _sessions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _byDebugSession = new(StringComparer.Ordinal);
	private readonly CounterHistory[] _histories = new CounterHistory[Counters.Count];
	private readonly double[] _previous = new double[Counters.Count];
	private readonly List<HeapSnapshot> _snapshots = [];
	private readonly Queue<int> _gcPips = new();

	private IWorkbenchViews? _views;
	private ICommandHost? _host;
	private IOutputChannel? _log;
	private long _lastFlush;
	private int _exceptionsInWindow;
	private int _nextId;
	private long _processesFetched;
	private bool _fetchingProcesses;
	private CancellationTokenSource? _shutdown = new();

	public DotnetProfilerService() {
		for (int i = 0; i < _histories.Length; i++) _histories[i] = new CounterHistory();

		Array.Fill(_previous, double.NaN);
	}

	public event Action<DotnetProfilerChange>? Changed;

	public Func<DotnetProfilerEditor> CreateEditor { get; set; } = static () => throw new InvalidOperationException("the profiler editor is not available");

	public DotnetProfilerOptions Options { get; private set; } = DotnetProfilerOptions.Read(Settings.Current);

	public bool ProfileOnStart { get; private set; }

	public ProfilingSession? Active { get; private set; }

	public string Status { get; private set; } = "no session";

	public bool Live => Active is not null;

	public bool Recording => Active?.Recording ?? false;

	public CpuProfile? Profile { get; private set; }

	public Views.ProfileViewState View { get; } = new();

	public GcHeapStats? GcStats { get; private set; }

	public IReadOnlyList<HeapSnapshot> Snapshots => _snapshots;

	public SnapshotDiff? Diff { get; private set; }

	public IReadOnlyList<ProcessInfo> Processes { get; private set; } = [];

	public int IntervalMs => Options.RefreshIntervalMs;

	public IEnumerable<int> GcPips => _gcPips;

	public CounterHistory History(Counter counter) => _histories[(int)counter];

	public double Readout(Counter counter) => _histories[(int)counter].Latest;

	public void Attach(IWorkbenchViews? views, ICommandHost host, IDebug? debug) {
		_views = views;
		_host = host;

		if (debug is null) return;

		debug.ProcessStarted += OnDebugProcess;
		debug.SessionEnded += OnDebugEnded;
	}

	public void Reconfigure(Settings settings) {
		DotnetProfilerOptions previous = Options;
		Options = DotnetProfilerOptions.Read(settings);

		if (previous.RefreshIntervalMs != Options.RefreshIntervalMs) SetInterval(Options.RefreshIntervalMs);

		if (previous.NamespaceFilter != Options.NamespaceFilter) PushFilter();

		Raise(DotnetProfilerChange.Options);
	}

	private void Log(string line) {
		if (_host is null) return;

		(_log ??= _host.Channel(".NET Profiler")).Append(line);
	}

	private void Raise(DotnetProfilerChange change) {
		if (_views is { } views) views.Post(() => Changed?.Invoke(change));
		else Changed?.Invoke(change);
	}

	private void Fail(string action, Exception exception) {
		Log($"{action}: {exception.Message}");
		_host?.ShowMessage(MessageSeverity.Error, $".NET Profiler: {exception.Message}", []);
	}

	#region Sessions

	public void OpenEditor() => _views?.OpenEditor(EditorId, CreateEditor);

	public void ShowGraphs() => _views?.ShowContainer(DotnetProfilerExtension.PanelContainer);

	public void ShowLive() => _views?.ShowContainer(DotnetProfilerExtension.SidebarContainer);

	public void RefreshProcesses(bool force = false) {
		lock (_gate) {
			if (_fetchingProcesses || (!force && Environment.TickCount64 - _processesFetched < 1000)) return;

			_fetchingProcesses = true;
		}

		_ = Task.Run(() => {
			List<ProcessInfo> found;

			try {
				found = ProcessDiscovery.List();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) {
				found = [];
			}

			lock (_gate) {
				_fetchingProcesses = false;
				_processesFetched = Environment.TickCount64;
			}

			Processes = found;
			Raise(DotnetProfilerChange.Processes);
		});
	}

	public void PickAndAttach() {
		if (_host is not { } host) return;

		_ = Task.Run(() => {
			List<ProcessInfo> processes = ProcessDiscovery.List();

			if (processes.Count == 0) {
				host.ShowMessage(MessageSeverity.Info, "No .NET processes found.", []);
				return;
			}

			QuickPickItem[] items = [.. processes.Select(static process => new QuickPickItem(process.Name, $"pid {process.Pid}", process.CommandLine ?? string.Empty))];
			host.ShowQuickPick("Attach .NET Profiler", items, index => {
				if ((uint)index < (uint)processes.Count) _ = AttachAsync(processes[index].Pid, processes[index].Name, null);
			});
		});
	}

	public async Task AttachAsync(int pid, string name, string? debugSession) {
		try {
			ReleaseAll("replaced by a new session");

			string id = $"s{Interlocked.Increment(ref _nextId)}";
			ProfilingSession session = new(id, pid, name, this);

			await Task.Run(() => {
				session.SetFilter(Options.NamespaceFilter);
				session.StartCounters(Options.RefreshIntervalMs);
			}).ConfigureAwait(false);

			lock (_gate) {
				_sessions[id] = session;

				if (debugSession is not null) _byDebugSession[debugSession] = id;
			}

			Log($"attached to {name} (pid {pid})");

			Post(() => {
				ResetData();
				Active = session;
				Status = $"{name} · pid {pid}";
				Changed?.Invoke(DotnetProfilerChange.Session | DotnetProfilerChange.Status | DotnetProfilerChange.Processes);

				if (Options.OpenPanelOnAttach) _views?.OpenEditor(EditorId, CreateEditor, preserveFocus: true);

				if (ProfileOnStart && debugSession is not null) StartCpu();
			});
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or ServerNotAvailableException) {
			Fail($"attach to pid {pid}", exception);
		}
	}

	private void ResetData() {
		foreach (CounterHistory history in _histories) history.Clear();

		Array.Fill(_previous, double.NaN);
		_exceptionsInWindow = 0;
		_lastFlush = 0;
		_gcPips.Clear();
		GcStats = null;
		Profile = null;
		View.Load(null);
		Diff = null;
		_snapshots.Clear();
	}

	public void Detach(string reason = "detached") {
		if (Active is { } active) Stop(active, reason);
	}

	private void ReleaseAll(string reason) {
		ProfilingSession[] sessions;

		lock (_gate) sessions = [.. _sessions.Values];

		foreach (ProfilingSession session in sessions) Stop(session, reason);
	}

	private void Stop(ProfilingSession session, string reason) {
		lock (_gate) {
			if (!_sessions.Remove(session.Id)) return;

			foreach ((string debugId, string mapped) in _byDebugSession.ToArray()) {
				if (mapped == session.Id) _byDebugSession.Remove(debugId);
			}
		}

		Log($"session {session.Id} ending: {reason}");
		_ = Task.Run(session.Dispose);

		Post(() => {
			if (!ReferenceEquals(Active, session)) return;

			lock (_gate) Active = _sessions.Values.LastOrDefault();

			Status = Active is { } next ? $"{next.Name} · pid {next.Pid}" : reason;
			Changed?.Invoke(DotnetProfilerChange.Session | DotnetProfilerChange.Status | DotnetProfilerChange.Recording | DotnetProfilerChange.Processes);
		});
	}

	private void OnDebugProcess(string debugSession, string name, int pid) {
		if (Options.AutoProfile == "off") return;

		lock (_gate) {
			if (_byDebugSession.ContainsKey(debugSession)) return;
		}

		Log($"debug session '{name}' -> pid {pid}");
		_ = AttachAsync(pid, name, debugSession);
	}

	private void OnDebugEnded(string debugSession) {
		string? id;

		lock (_gate) _byDebugSession.TryGetValue(debugSession, out id);

		if (id is null) return;

		ProfilingSession? session;

		lock (_gate) _sessions.TryGetValue(id, out session);

		if (session is not null) Stop(session, "debug session ended");
	}

	public void SetProfileOnStart(bool enabled) {
		ProfileOnStart = enabled;
		Log($"profile on debug start -> {(enabled ? "on" : "off")}");
		Raise(DotnetProfilerChange.Options);
	}

	public void SetNamespaceFilter(string filter) {
		string next = filter.Trim();

		if (next == Options.NamespaceFilter) return;

		Settings.Current.SetValue("dotnetProfiler.namespaceFilter", next.Length == 0 ? null : JsonValue.Create(next));
		Options = Options with { NamespaceFilter = next };
		PushFilter();
		Raise(DotnetProfilerChange.Options);
	}

	private void PushFilter() {
		ProfilingSession[] sessions;

		lock (_gate) sessions = [.. _sessions.Values];

		foreach (ProfilingSession session in sessions) session.SetFilter(Options.NamespaceFilter);

		Log($"namespace filter -> {(Options.NamespaceFilter.Length == 0 ? "(none)" : Options.NamespaceFilter)}");
	}

	public void SetInterval(int intervalMs) {
		intervalMs = Math.Clamp(intervalMs, 50, 10_000);

		if (intervalMs != Options.RefreshIntervalMs) {
			Settings.Current.SetValue("dotnetProfiler.refreshIntervalMs", JsonValue.Create(intervalMs));
			Options = Options with { RefreshIntervalMs = intervalMs };
		}

		ProfilingSession[] sessions;

		lock (_gate) sessions = [.. _sessions.Values];

		_ = Task.Run(() => {
			foreach (ProfilingSession session in sessions) session.SetInterval(intervalMs);
		});

		Log($"counter refresh interval -> {intervalMs}ms");
		Raise(DotnetProfilerChange.Options);
	}

	public void PickInterval() {
		int[] values = [100, 250, 500, 1000, 2000];
		QuickPickItem[] items = [new("100 ms", "very responsive, highest overhead"), new("250 ms", "default"), new("500 ms", "smoother, lower overhead"), new("1000 ms", "dotnet-counters default"), new("2000 ms", "background monitoring")];

		_host?.ShowQuickPick("Live graph refresh interval", items, index => {
			if ((uint)index < (uint)values.Length) SetInterval(values[index]);
		});
	}

	#endregion

	#region Recording

	public void ToggleRecording() {
		if (Recording) _ = StopCpuAsync();
		else StartCpu();
	}

	public void StartCpu() {
		if (Active is not { } session || session.Recording) return;

		try {
			session.StartCpu(Options.TrackAllocations, Options.IncludeBlockedThreads);
			Log($"recording {session.Id}{(Options.NamespaceFilter.Length > 0 ? $" limited to {Options.NamespaceFilter}" : string.Empty)}");
			Raise(DotnetProfilerChange.Recording);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) {
			Fail("start recording", exception);
		}
	}

	public async Task StopCpuAsync() {
		if (Active is not { Recording: true } session) return;

		Status = "Resolving symbols...";
		Raise(DotnetProfilerChange.Recording | DotnetProfilerChange.Status);

		try {
			CpuProfile profile = await session.StopCpuAsync(_shutdown?.Token ?? default).ConfigureAwait(false);
			Log($"profile: {profile.TotalSamples} samples, {profile.Cpu.Count - 1} frames");
			Post(() => {
				Profile = profile;
				View.Load(profile);
				Changed?.Invoke(DotnetProfilerChange.Profile);
			});
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException) {
			Fail("stop recording", exception);
		}
		finally {
			Status = $"{session.Name} · pid {session.Pid}";
			Raise(DotnetProfilerChange.Recording | DotnetProfilerChange.Status);
		}
	}

	public async Task SnapshotAsync() {
		if (Active is not { } session) return;

		Status = "Capturing heap snapshot...";
		Raise(DotnetProfilerChange.Status);

		try {
			HeapSnapshot snapshot = await session.SnapshotAsync(_shutdown?.Token ?? default).ConfigureAwait(false);

			if (_views is { } views) views.Post(() => Add(snapshot));
			else Add(snapshot);

			Log($"snapshot {snapshot.SnapshotId}: {snapshot.TotalObjects} objects, {Format.Bytes(snapshot.TotalBytes)}, {snapshot.TypeCount} types");

			if (snapshot.SkippedObjects > 0) Log($"{snapshot.SkippedObjects} objects were unreadable and excluded from the snapshot");
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or OperationCanceledException) {
			Fail("heap snapshot", exception);
		}
		finally {
			Status = $"{session.Name} · pid {session.Pid}";
			Raise(DotnetProfilerChange.Status);
		}

		void Add(HeapSnapshot snapshot) {
			_snapshots.Add(snapshot);
			Changed?.Invoke(DotnetProfilerChange.Snapshots);
		}
	}

	public void CompareSnapshots() {
		if (Active is not { } session) return;

		try {
			Diff = session.CompareLastTwo();
			Log($"diff {Diff.BaselineId} -> {Diff.CurrentId}: {Format.SignedBytes(Diff.ByteDelta)} across {Diff.ObjectDelta:+#;-#;0} objects");
			Raise(DotnetProfilerChange.Diff);
		}
		catch (InvalidOperationException exception) {
			Fail("compare snapshots", exception);
		}
	}

	public void CleanTempFiles() {
		if (_host is not { } host) return;

		TempSurvey survey = TempFiles.Survey();

		if (survey.Count == 0) {
			host.ShowMessage(MessageSeverity.Info, "No profiler temp files to clean.", []);
			return;
		}

		host.ShowMessage(MessageSeverity.Warning, $"Delete {survey.Count} profiler temp file(s) totalling {Format.Bytes(survey.Bytes)} from {survey.Directory}?", ["Delete"], choice => {
			if (choice != 0) return;

			TempCleanup result = TempFiles.Cleanup(TimeSpan.Zero);
			string locked = result.Locked > 0 ? $" ({result.Locked} in use, skipped)" : string.Empty;
			host.ShowMessage(MessageSeverity.Info, $"Deleted {result.Deleted} file(s), freed {Format.Bytes(result.Bytes)}{locked}.", []);
		});
	}

	#endregion

	#region Sink

	void ISessionSink.OnCounters(ProfilingSession session, ReadOnlySpan<double> values) {
		if (!ReferenceEquals(session, Active)) return;

		double[] copy = values.ToArray();
		long now = Environment.TickCount64;

		Post(() => {
			double seconds = _lastFlush == 0 ? Options.RefreshIntervalMs / 1000.0 : (now - _lastFlush) / 1000.0;
			_lastFlush = now;

			for (int i = 0; i < copy.Length; i++) {
				double raw = copy[i];

				if (double.IsNaN(raw)) continue;

				Counter counter = (Counter)i;
				double scaled = raw * Counters.Scale(counter);

				if (!Counters.IsCumulative(counter)) {
					_histories[i].Push(scaled);
					continue;
				}

				double previous = _previous[i];
				_previous[i] = scaled;

				if (!double.IsNaN(previous) && seconds > 0) _histories[i].Push(Math.Max(0, (scaled - previous) / seconds));
			}

			if (seconds > 0) _histories[(int)Counter.Exceptions].Push(_exceptionsInWindow / seconds);

			_exceptionsInWindow = 0;
			Changed?.Invoke(DotnetProfilerChange.Counters);
		});
	}

	void ISessionSink.OnGcStats(ProfilingSession session, GcHeapStats stats) {
		if (ReferenceEquals(session, Active)) GcStats = stats;
	}

	void ISessionSink.OnGcEvent(ProfilingSession session, int generation, int count) {
		if (!ReferenceEquals(session, Active)) return;

		Post(() => {
			_gcPips.Enqueue(generation);

			while (_gcPips.Count > GcPipCapacity) _gcPips.Dequeue();

			_histories[(int)Counter.GcHeapSize].Mark(generation);
			_histories[(int)Counter.WorkingSet].Mark(generation);
			_histories[(int)Counter.AllocRate].Mark(generation);
		});
	}

	void ISessionSink.OnException(ProfilingSession session, string type, string message) {
		if (ReferenceEquals(session, Active)) Interlocked.Increment(ref _exceptionsInWindow);
	}

	void ISessionSink.OnLog(string message) => Log(message);

	void ISessionSink.OnCountersEnded(ProfilingSession session) {
		if (!ReferenceEquals(session, Active)) return;

		Post(() => {
			if (ReferenceEquals(session, Active)) Stop(session, "process exited");
		});
	}

	private void Post(Action action) {
		if (_views is { } views) views.Post(action);
		else action();
	}

	#endregion

	public void Dispose() {
		_shutdown?.Cancel();
		_shutdown = null;
		ReleaseAll("shutting down");
	}
}

public sealed record DotnetProfilerOptions(string AutoProfile, bool OpenPanelOnAttach, int RefreshIntervalMs, int ProcessListRefreshMs, string NamespaceFilter, bool TrackAllocations, bool IncludeBlockedThreads, bool InlineAnnotations)
{
	public static DotnetProfilerOptions Read(Settings settings) => new(
		settings.GetString("dotnetProfiler.autoProfile") ?? "counters",
		settings.GetBoolean("dotnetProfiler.openPanelOnAttach") ?? true,
		Math.Clamp(settings.GetInt32("dotnetProfiler.refreshIntervalMs") ?? 250, 50, 10_000),
		Math.Max(0, settings.GetInt32("dotnetProfiler.processListRefreshMs") ?? 5000),
		(settings.GetString("dotnetProfiler.namespaceFilter") ?? string.Empty).Trim(),
		settings.GetBoolean("dotnetProfiler.trackAllocations") ?? true,
		settings.GetBoolean("dotnetProfiler.includeBlockedThreads") ?? false,
		settings.GetBoolean("dotnetProfiler.inlineAnnotations") ?? true);
}
