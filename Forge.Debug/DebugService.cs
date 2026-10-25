using System.Diagnostics;
using System.Text.Json.Nodes;

using Forge.Common.Debug;

namespace Forge.Debug;

public sealed class DebugService : IDebug, IDisposable
{
	private readonly BreakpointStore _breakpoints = new();
	private readonly List<string> _watches = [];
	private readonly List<DebugStackFrame> _callStack = [];

	private LaunchConfigurations _launch = new();
	private DebugSession? _session;
	private string? _selected;
	private int _focusedFrameId = -1;
	private int _sessionCounter;
	private bool _disposed;
	private bool _acquiring;
	private string? _pendingLaunch;
	private bool _ignoreBreakpointChanges;
	private readonly HashSet<string> _sentPaths = new(StringComparer.OrdinalIgnoreCase);

	private Action? _stateChanged;
	private Action? _modelChanged;
	private Action<DebugOutput>? _output;

	public DebugService() => _breakpoints.Changed += () => {
		if (!_ignoreBreakpointChanges) _ = SendBreakpointsAsync();

		_modelChanged?.Invoke();
	};

	public string WorkspaceFolder { get; private set; } = string.Empty;

	public Func<string, string, string?, Action<int>, bool>? TaskRunner { get; set; }

	public Func<ResolverContext>? ContextProvider { get; set; }

	public Action<DebugReveal>? RevealSource { get; set; }

	public Action? ExceptionCleared { get; set; }

	public DebugSession? Session => _session;

	public void Attach(string workspaceFolder, string breakpointStatePath) {
		WorkspaceFolder = workspaceFolder;
		_breakpoints.Load(breakpointStatePath);
		ReloadConfigurations();
	}

	public DebugState GetState() => _session?.State ?? DebugState.Inactive;

	public bool IsActive() => _session is not null && _session.State != DebugState.Inactive;

	public IReadOnlyList<DebugConfiguration> GetConfigurations() => _launch.Configurations;

	public IReadOnlyList<DebugCompound> GetCompounds() => _launch.Compounds;

	public string? GetSelectedConfiguration() => _selected;

	public void SelectConfiguration(string name) {
		_selected = name;
		_modelChanged?.Invoke();
	}

	public void ReloadConfigurations() {
		_launch = LaunchConfigurations.Load(WorkspaceFolder);

		if (_selected is null || _launch.Find(_selected) is null) {
			_selected = _launch.Configurations.Count > 0 ? _launch.Configurations[0].Name : null;
		}

		_modelChanged?.Invoke();
	}

	public bool StartDebugging(string? configurationName, bool noDebug) =>
		StartDebugging(configurationName, noDebug, skipPreLaunch: false);

	private bool StartDebugging(string? configurationName, bool noDebug, bool skipPreLaunch) {
		if (_session is not null) {
			Report("A debug session is already running.");
			return false;
		}

		string? name = configurationName ?? _selected;

		if (name is null || _launch.Find(name) is not { } configuration) {
			Report($"No launch configuration named \"{name}\" in .vscode/launch.json.");
			return false;
		}

		ResolverContext context = ContextProvider?.Invoke() ?? new ResolverContext(WorkspaceFolder);
		DebugConfiguration resolved = LaunchConfigurations.Resolve(configuration, context);

		if (!skipPreLaunch && resolved.PreLaunchTask is { Length: > 0 } preLaunch) {
			if (_pendingLaunch is not null) return false;

			_pendingLaunch = name;

			if (!RunTaskByLabel(preLaunch, resolved.Cwd, exitCode => {
				_pendingLaunch = null;

				if (exitCode != 0) {
					Report($"The preLaunchTask \"{preLaunch}\" exited with code {exitCode}.");
					return;
				}

				StartDebugging(name, noDebug, skipPreLaunch: true);
			})) {
				_pendingLaunch = null;
				return false;
			}

			return false;
		}

		if (DebugAdapters.StartInfoFor(resolved.Type, resolved.Cwd ?? WorkspaceFolder) is not { } adapter) {
			if (DebugAdapters.For(resolved.Type) is not { Acquire: { } acquire }) {
				Report($"No extension provides a debug adapter for \"{resolved.Type}\".");
				return false;
			}

			if (_acquiring) {
				Report("Still installing the debug adapter.");
				return false;
			}

			_acquiring = true;
			Report("Installing the debug adapter...");
			_ = AcquireThenStartAsync(acquire, name, noDebug);
			return false;
		}

		if (noDebug && resolved.Raw is { } raw) raw["noDebug"] = true;

		DebugSession session = new($"session-{++_sessionCounter}", resolved, adapter);

		session.StateChanged += state => {
			if (state != DebugState.Stopped) ClearCallStack();

			_stateChanged?.Invoke();
		};

		session.SendBreakpoints = SendBreakpointsAsync;
		session.Output += value => _output?.Invoke(value);
		session.Log += line => _output?.Invoke(new DebugOutput("console", line + "\n", null, 0));
		session.ThreadsChanged += () => _modelChanged?.Invoke();

		session.Stopped += (_, _) => {
			_ = RefreshCallStackAsync(session);
			_stateChanged?.Invoke();
		};

		session.Continued += () => {
			ClearCallStack();
			_stateChanged?.Invoke();
		};

		session.Terminated += reason => {
			if (reason is { Length: > 0 }) Report(reason);

			if (!ReferenceEquals(_session, session)) return;

			_session = null;
			ClearCallStack();
			_stateChanged?.Invoke();

			if (resolved.PostDebugTask is { Length: > 0 } after) {
				RunTaskByLabel(after, resolved.Cwd, null);
			}

			session.Dispose();
		};

		session.RunInTerminalRequested += (commandLine, cwd) =>
			TaskRunner?.Invoke(session.Configuration.Name, commandLine, cwd, null!);

		_session = session;
		_focusedFrameId = -1;

		_ = StartSessionAsync(session);
		_stateChanged?.Invoke();
		return true;
	}

	private async Task AcquireThenStartAsync(Func<Action<string>, Task<bool>> acquire,
		string configurationName, bool noDebug) {
		bool acquired = await acquire(Report).ConfigureAwait(false);

		_acquiring = false;

		if (acquired) StartDebugging(configurationName, noDebug, skipPreLaunch: true);
	}

	private bool RunTaskByLabel(string label, string? cwd, Action<int>? finished) {
		if (TaskRunner is not { } runner) return false;

		TaskConfigurations tasks = TaskConfigurations.Load(WorkspaceFolder);

		if (tasks.Find(label) is not { } task) {
			Report($"Could not find the task \"{label}\" in .vscode/tasks.json.");
			return false;
		}

		IReadOnlyList<TaskDefinition> chain = tasks.Chain(task);

		ResolverContext context = ContextProvider?.Invoke() ?? new ResolverContext(WorkspaceFolder);

		for (int i = 0; i < chain.Count; i++) {
			TaskDefinition step = TaskConfigurations.Resolve(chain[i], context);

			if (step.CommandLine is not { Length: > 0 } commandLine) continue;

			bool last = i == chain.Count - 1;

			runner(step.Label, commandLine, step.Cwd ?? cwd ?? WorkspaceFolder,
				last && finished is not null ? finished : _ => { });
		}

		return true;
	}

	private void Report(string message) =>
		_output?.Invoke(new DebugOutput("console", message + (char)10, null, 0));

	private async Task StartSessionAsync(DebugSession session) {
		if (!await session.StartAsync().ConfigureAwait(false)) return;

		_ignoreBreakpointChanges = true;

		try {
			_breakpoints.SetExceptionFilters(session.Capabilities.ExceptionFilters);
		}
		finally {
			_ignoreBreakpointChanges = false;
		}
	}

	public void RestartSession() {
		if (_session is not { } session) return;

		string name = session.Configuration.Name;

		StopSession(disconnect: false, suspend: false);
		StartDebugging(name, noDebug: false);
	}

	public void StopSession(bool disconnect, bool suspend) {
		if (_session is not { } session) return;

		_ = session.StopAsync(disconnect, suspend);
	}

	public void Continue() => WithThread((session, thread) => session.ContinueAsync(thread));

	public void Pause() => WithThread((session, thread) => session.PauseAsync(thread));

	public void StepOver() => WithThread((session, thread) => session.NextAsync(thread));

	public void StepInto() => WithThread((session, thread) => session.StepInAsync(thread));

	public void StepOut() => WithThread((session, thread) => session.StepOutAsync(thread));

	public void StepBack() => WithThread((session, thread) => session.StepBackAsync(thread));

	public void ReverseContinue() => WithThread((session, thread) => session.ReverseContinueAsync(thread));

	public IReadOnlyList<SourceBreakpoint> GetBreakpoints() => _breakpoints.Source;

	public IReadOnlyList<SourceBreakpoint> GetBreakpointsFor(string path) => _breakpoints.For(path);

	public SourceBreakpoint? FindBreakpoint(string path, int line) => _breakpoints.Find(path, line);

	public bool ToggleBreakpoint(string path, int line) => _breakpoints.Toggle(path, line);

	public void AddBreakpoint(SourceBreakpoint breakpoint) => _breakpoints.Add(breakpoint);

	public void UpdateBreakpoint(string path, int line, string? condition, string? hitCondition,
		string? logMessage) => _breakpoints.Update(path, line, condition, hitCondition, logMessage);

	public void RemoveBreakpoint(string path, int line) => _breakpoints.Remove(path, line);

	public void RemoveAllBreakpoints() => _breakpoints.RemoveAll();

	public void EnableOrDisableBreakpoints(bool enable, string? path, int line) =>
		_breakpoints.SetEnabled(enable, path, line);

	public void SetBreakpointsActivated(bool activated) => _breakpoints.SetActivated(activated);

	public bool AreBreakpointsActivated() => _breakpoints.Activated;

	public IReadOnlyList<FunctionBreakpoint> GetFunctionBreakpoints() => _breakpoints.Functions;

	public void AddFunctionBreakpoint(string name) => _breakpoints.AddFunction(name);

	public void RemoveFunctionBreakpoint(string name) => _breakpoints.RemoveFunction(name);

	public IReadOnlyList<ExceptionBreakpoint> GetExceptionBreakpoints() => _breakpoints.Exceptions;

	public void SetExceptionBreakpoint(string filter, bool enabled, string? condition) =>
		_breakpoints.SetException(filter, enabled, condition);

	public IReadOnlyList<DebugThread> GetThreads() => _session?.Threads ?? [];

	public IReadOnlyList<DebugStackFrame> GetCallStack() {
		lock (_callStack) return [.. _callStack];
	}

	public DebugStackFrame? GetFocusedFrame() {
		lock (_callStack) {
			foreach (DebugStackFrame frame in _callStack) {
				if (frame.Id == _focusedFrameId) return frame;
			}

			return _callStack.Count > 0 ? _callStack[0] : null;
		}
	}

	public void FocusStackFrame(int frameId) {
		_focusedFrameId = frameId;

		if (GetFocusedFrame() is { SourcePath: { Length: > 0 } path } frame) RevealSource?.Invoke(new DebugReveal(path, frame.Line, frame.Column, IsTopFrame(frame), null));

		_modelChanged?.Invoke();
	}

	public IReadOnlyList<string> GetWatchExpressions() {
		lock (_watches) return [.. _watches];
	}

	public void AddWatchExpression(string expression) {
		lock (_watches) _watches.Add(expression);

		_modelChanged?.Invoke();
	}

	public void RenameWatchExpression(int index, string expression) {
		lock (_watches) {
			if (index < 0 || index >= _watches.Count) return;

			_watches[index] = expression;
		}

		_modelChanged?.Invoke();
	}

	public void RemoveWatchExpression(int index) {
		lock (_watches) {
			if (index < 0 || index >= _watches.Count) return;

			_watches.RemoveAt(index);
		}

		_modelChanged?.Invoke();
	}

	public void RemoveAllWatchExpressions() {
		lock (_watches) _watches.Clear();

		_modelChanged?.Invoke();
	}

	public void OnStateChanged(Action handler) {
		_stateChanged -= handler;
		_stateChanged += handler;
	}

	public void OnModelChanged(Action handler) {
		_modelChanged -= handler;
		_modelChanged += handler;
	}

	public void OnOutput(Action<DebugOutput> handler) {
		_output -= handler;
		_output += handler;
	}

	public Task<IReadOnlyList<DebugScope>> ScopesAsync(int frameId) =>
		_session?.ScopesAsync(frameId) ?? Task.FromResult<IReadOnlyList<DebugScope>>([]);

	public Task<IReadOnlyList<DebugVariable>> VariablesAsync(int variablesReference) =>
		_session?.VariablesAsync(variablesReference) ?? Task.FromResult<IReadOnlyList<DebugVariable>>([]);

	public Task<DebugVariable?> EvaluateAsync(string expression, string context) =>
		_session?.EvaluateAsync(expression, GetFocusedFrame()?.Id, context)
			?? Task.FromResult<DebugVariable?>(null);

	private void WithThread(Func<DebugSession, int, Task> action) {
		if (_session is not { } session) return;

		int thread = session.StoppedThreadId ?? (session.Threads.Count > 0 ? session.Threads[0].Id : 0);

		if (thread == 0) return;

		_ = action(session, thread);
	}

	private async Task RefreshCallStackAsync(DebugSession session) {
		if (session.StoppedThreadId is not { } thread) return;

		IReadOnlyList<DebugStackFrame> frames = await session.StackTraceAsync(thread, 0, 200)
			.ConfigureAwait(false);

		lock (_callStack) {
			_callStack.Clear();
			_callStack.AddRange(frames);
		}

		if (frames.Count > 0) {
			_focusedFrameId = frames[0].Id;

			DebugExceptionInfo? exception = session.StopReason == "exception" ? await session.ExceptionInfoAsync(thread).ConfigureAwait(false) : null;

			if (frames[0].SourcePath is { Length: > 0 } path) RevealSource?.Invoke(new DebugReveal(path, frames[0].Line, frames[0].Column, true, exception));
		}

		_modelChanged?.Invoke();
	}

	private void ClearCallStack() {
		lock (_callStack) _callStack.Clear();

		_focusedFrameId = -1;
		ExceptionCleared?.Invoke();
		_modelChanged?.Invoke();
	}

	private bool IsTopFrame(DebugStackFrame frame) {
		lock (_callStack) return _callStack.Count > 0 && _callStack[0].Id == frame.Id;
	}

	private Task SendBreakpointsAsync() =>
		_session is { } session ? SendBreakpointsAsync(session) : Task.CompletedTask;

	private async Task SendBreakpointsAsync(DebugSession session) {
		if (!session.IsRunning) return;

		bool activated = _breakpoints.Activated;

		HashSet<string> current = new(_breakpoints.Paths, StringComparer.OrdinalIgnoreCase);

		foreach (string cleared in _sentPaths) {
			if (current.Contains(cleared)) continue;

			await session.SetBreakpointsAsync(cleared, []).ConfigureAwait(false);
		}

		_sentPaths.Clear();

		foreach (string path in current) {
			_sentPaths.Add(path);
			JsonArray payload = [];
			List<int> lines = [];

			foreach (SourceBreakpoint breakpoint in _breakpoints.For(path)) {
				if (!activated || !breakpoint.Enabled) continue;

				JsonObject entry = new() { ["line"] = breakpoint.Line };

				if (breakpoint.Condition is { Length: > 0 } condition &&
					session.Capabilities.ConditionalBreakpoints) entry["condition"] = condition;

				if (breakpoint.HitCondition is { Length: > 0 } hit &&
					session.Capabilities.HitConditionalBreakpoints) entry["hitCondition"] = hit;

				if (breakpoint.LogMessage is { Length: > 0 } log &&
					session.Capabilities.LogPoints) entry["logMessage"] = log;

				payload.Add(entry);
				lines.Add(breakpoint.Line);
			}

			JsonNode? response = await session.SetBreakpointsAsync(path, payload).ConfigureAwait(false);

			if (response?["breakpoints"] is not JsonArray verified) continue;

			List<(int, bool, int?, string?)> results = [];

			for (int i = 0; i < verified.Count && i < lines.Count; i++) {
				results.Add((lines[i],
					verified[i]?["verified"]?.GetValue<bool>() ?? false,
					verified[i]?["line"]?.GetValue<int>(),
					verified[i]?["message"]?.GetValue<string>()));
			}

			_ignoreBreakpointChanges = true;

			try {
				_breakpoints.Verify(path, results);
			}
			finally {
				_ignoreBreakpointChanges = false;
			}
		}

		JsonArray functions = [];

		foreach (FunctionBreakpoint breakpoint in _breakpoints.Functions) {
			if (!activated || !breakpoint.Enabled) continue;

			functions.Add(new JsonObject { ["name"] = breakpoint.Name });
		}

		await session.SetFunctionBreakpointsAsync(functions).ConfigureAwait(false);

		JsonArray filters = [];

		foreach (ExceptionBreakpoint breakpoint in _breakpoints.Exceptions) {
			if (breakpoint.Enabled) filters.Add(breakpoint.Filter);
		}

		await session.SetExceptionBreakpointsAsync(filters).ConfigureAwait(false);
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;
		_breakpoints.Save();
		_session?.Dispose();
		_session = null;
	}
}
