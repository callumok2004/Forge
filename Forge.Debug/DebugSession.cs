using System.Diagnostics;
using System.Text.Json.Nodes;

using Forge.Common.Debug;

namespace Forge.Debug;

public sealed class DebugSession : IDisposable
{
	private readonly DapConnection _connection;
	private readonly List<DebugThread> _threads = [];
	private readonly Lock _sync = new();

	private bool _configurationDone;
	private bool _disposed;

	public DebugSession(string id, DebugConfiguration configuration, ProcessStartInfo adapter) {
		Id = id;
		Configuration = configuration;

		_connection = new DapConnection(adapter);
		_connection.Event += OnEvent;
		_connection.Log += line => Log?.Invoke(line);
		_connection.Exited += code => Terminate($"The debug adapter exited with code {code}.");
		_connection.RequestHandler = OnReverseRequest;
	}

	public string Id { get; }

	public DebugConfiguration Configuration { get; }

	public DebugCapabilities Capabilities { get; private set; } = new();

	public DebugState State { get; private set; } = DebugState.Inactive;

	public int? StoppedThreadId { get; private set; }

	public string? StopReason { get; private set; }

	public event Action<DebugState>? StateChanged;

	public event Action<DebugOutput>? Output;

	public event Action<string>? Log;

	public event Action? ThreadsChanged;

	public Func<DebugSession, Task>? SendBreakpoints { get; set; }

	public event Action<int, string>? Stopped;

	public event Action? Continued;

	public event Action<string?>? Terminated;

	public event Action<JsonNode?>? BreakpointChanged;

	public event Action<string, string?>? RunInTerminalRequested;

	public IReadOnlyList<DebugThread> Threads {
		get { lock (_sync) return [.. _threads]; }
	}

	public async Task<bool> StartAsync(CancellationToken cancellationToken = default) {
		SetState(DebugState.Initializing);

		JsonObject arguments = new() {
			["clientID"] = "forge",
			["clientName"] = "Forge",
			["adapterID"] = Configuration.Type,
			["locale"] = "en",
			["linesStartAt1"] = true,
			["columnsStartAt1"] = true,
			["pathFormat"] = "path",
			["supportsVariableType"] = true,
			["supportsVariablePaging"] = true,
			["supportsRunInTerminalRequest"] = true,
			["supportsMemoryReferences"] = true,
			["supportsProgressReporting"] = false,
			["supportsInvalidatedEvent"] = true,
			["supportsMemoryEvent"] = true,
		};

		try {
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			timeout.CancelAfter(TimeSpan.FromSeconds(20));

			JsonNode? body = await _connection.SendAsync("initialize", arguments, timeout.Token)
				.ConfigureAwait(false);

			Capabilities = ReadCapabilities(body);

			JsonObject launch = Configuration.Raw is { } raw
				? (JsonObject)raw.DeepClone()
				: new JsonObject { ["name"] = Configuration.Name, ["type"] = Configuration.Type };

			launch["__sessionId"] = Id;

			await _connection.SendAsync(Configuration.Request == "attach" ? "attach" : "launch",
				launch, timeout.Token).ConfigureAwait(false);

			return true;
		}
		catch (Exception exception) when (exception is DapException or OperationCanceledException) {
			Log?.Invoke(exception is OperationCanceledException
				? $"{Configuration.Name}: the adapter did not respond within 20 seconds."
				: $"{Configuration.Name} failed to start: {exception.Message}");
			Terminate(exception.Message);
			return false;
		}
	}

	public async Task ConfigurationDoneAsync(CancellationToken cancellationToken = default) {
		if (_configurationDone) return;

		_configurationDone = true;

		if (Capabilities.ConfigurationDone) {
			await SafeAsync("configurationDone", null, cancellationToken).ConfigureAwait(false);
		}

		SetState(DebugState.Running);
	}

	private async Task InitializedAsync() {
		if (SendBreakpoints is { } send) {
			try {
				await send(this).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is DapException or InvalidOperationException) {
				Log?.Invoke($"Could not send breakpoints: {exception.Message}");
			}
		}

		await ConfigurationDoneAsync().ConfigureAwait(false);
	}

	public Task<JsonNode?> SendAsync(string command, JsonNode? arguments,
		CancellationToken cancellationToken = default) =>
		_connection.SendAsync(command, arguments, cancellationToken);

	public async Task RefreshThreadsAsync(CancellationToken cancellationToken = default) {
		JsonNode? body = await SafeAsync("threads", null, cancellationToken).ConfigureAwait(false);

		List<DebugThread> refreshed = [];

		if (body?["threads"] is JsonArray array) {
			foreach (JsonNode? entry in array) {
				if (entry is null) continue;

				int id = entry["id"]?.GetValue<int>() ?? 0;
				bool stopped = StoppedThreadId == id;

				refreshed.Add(new DebugThread(id, entry["name"]?.GetValue<string>() ?? $"Thread {id}",
					stopped, stopped ? StopReason : null));
			}
		}

		lock (_sync) {
			_threads.Clear();
			_threads.AddRange(refreshed);
		}

		ThreadsChanged?.Invoke();
	}

	public async Task<IReadOnlyList<DebugStackFrame>> StackTraceAsync(int threadId,
		int startFrame = 0, int levels = 0, CancellationToken cancellationToken = default) {
		JsonObject arguments = new() { ["threadId"] = threadId, ["startFrame"] = startFrame };

		if (levels > 0) arguments["levels"] = levels;

		JsonNode? body = await SafeAsync("stackTrace", arguments, cancellationToken).ConfigureAwait(false);

		List<DebugStackFrame> frames = [];

		if (body?["stackFrames"] is JsonArray array) {
			foreach (JsonNode? entry in array) {
				if (entry is null) continue;

				frames.Add(new DebugStackFrame(
					entry["id"]?.GetValue<int>() ?? 0,
					entry["name"]?.GetValue<string>() ?? string.Empty,
					entry["source"]?["path"]?.GetValue<string>(),
					entry["line"]?.GetValue<int>() ?? 0,
					entry["column"]?.GetValue<int>() ?? 0,
					entry["presentationHint"]?.GetValue<string>(),
					entry["instructionPointerReference"]?.GetValue<string>()));
			}
		}

		return frames;
	}

	public async Task<IReadOnlyList<DebugScope>> ScopesAsync(int frameId,
		CancellationToken cancellationToken = default) {
		JsonNode? body = await SafeAsync("scopes", new JsonObject { ["frameId"] = frameId },
			cancellationToken).ConfigureAwait(false);

		List<DebugScope> scopes = [];

		if (body?["scopes"] is JsonArray array) {
			foreach (JsonNode? entry in array) {
				if (entry is null) continue;

				scopes.Add(new DebugScope(
					entry["name"]?.GetValue<string>() ?? string.Empty,
					entry["variablesReference"]?.GetValue<int>() ?? 0,
					entry["expensive"]?.GetValue<bool>() ?? false,
					entry["presentationHint"]?.GetValue<string>()));
			}
		}

		return scopes;
	}

	public async Task<IReadOnlyList<DebugVariable>> VariablesAsync(int variablesReference,
		CancellationToken cancellationToken = default) {
		JsonNode? body = await SafeAsync("variables",
			new JsonObject { ["variablesReference"] = variablesReference }, cancellationToken)
			.ConfigureAwait(false);

		List<DebugVariable> variables = [];

		if (body?["variables"] is JsonArray array) {
			foreach (JsonNode? entry in array) {
				if (entry is null) continue;

				variables.Add(new DebugVariable(
					entry["name"]?.GetValue<string>() ?? string.Empty,
					entry["value"]?.GetValue<string>() ?? string.Empty,
					entry["type"]?.GetValue<string>(),
					entry["variablesReference"]?.GetValue<int>() ?? 0,
					entry["evaluateName"]?.GetValue<string>(),
					entry["presentationHint"]?["attributes"] is JsonArray attributes &&
						attributes.Any(static value => value?.GetValue<string>() == "readOnly"),
					entry["namedVariables"]?.GetValue<int>() ?? 0,
					entry["indexedVariables"]?.GetValue<int>() ?? 0));
			}
		}

		return variables;
	}

	public async Task<DebugVariable?> EvaluateAsync(string expression, int? frameId, string context,
		CancellationToken cancellationToken = default) {
		JsonObject arguments = new() { ["expression"] = expression, ["context"] = context };

		if (frameId is { } frame) arguments["frameId"] = frame;

		JsonNode? body = await SafeAsync("evaluate", arguments, cancellationToken).ConfigureAwait(false);

		if (body is null) return null;

		return new DebugVariable(expression,
			body["result"]?.GetValue<string>() ?? string.Empty,
			body["type"]?.GetValue<string>(),
			body["variablesReference"]?.GetValue<int>() ?? 0,
			expression, false,
			body["namedVariables"]?.GetValue<int>() ?? 0,
			body["indexedVariables"]?.GetValue<int>() ?? 0);
	}

	public async Task<DebugExceptionInfo?> ExceptionInfoAsync(int threadId, CancellationToken cancellationToken = default) {
		if (!Capabilities.ExceptionInfo) return null;

		JsonNode? body = await SafeAsync("exceptionInfo", new JsonObject { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);

		if (body?["exceptionId"]?.GetValue<string>() is not { } id) return null;

		return new DebugExceptionInfo(id, body["description"]?.GetValue<string>(), body["breakMode"]?.GetValue<string>() ?? "always", body["details"]?["stackTrace"]?.GetValue<string>());
	}

	public Task ContinueAsync(int threadId, CancellationToken cancellationToken = default) =>
		SafeAsync("continue", new JsonObject { ["threadId"] = threadId }, cancellationToken);

	public Task PauseAsync(int threadId, CancellationToken cancellationToken = default) =>
		SafeAsync("pause", new JsonObject { ["threadId"] = threadId }, cancellationToken);

	public Task NextAsync(int threadId, CancellationToken cancellationToken = default) =>
		SafeAsync("next", new JsonObject { ["threadId"] = threadId }, cancellationToken);

	public Task StepInAsync(int threadId, CancellationToken cancellationToken = default) =>
		SafeAsync("stepIn", new JsonObject { ["threadId"] = threadId }, cancellationToken);

	public Task StepOutAsync(int threadId, CancellationToken cancellationToken = default) =>
		SafeAsync("stepOut", new JsonObject { ["threadId"] = threadId }, cancellationToken);

	public Task StepBackAsync(int threadId, CancellationToken cancellationToken = default) =>
		SafeAsync("stepBack", new JsonObject { ["threadId"] = threadId }, cancellationToken);

	public Task ReverseContinueAsync(int threadId, CancellationToken cancellationToken = default) =>
		SafeAsync("reverseContinue", new JsonObject { ["threadId"] = threadId }, cancellationToken);

	public Task RestartFrameAsync(int frameId, CancellationToken cancellationToken = default) =>
		SafeAsync("restartFrame", new JsonObject { ["frameId"] = frameId }, cancellationToken);

	public async Task StopAsync(bool disconnect, bool suspend,
		CancellationToken cancellationToken = default) {
		if (!disconnect && Capabilities.TerminateRequest) {
			await SafeAsync("terminate", new JsonObject { ["restart"] = false }, cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		JsonObject arguments = new() { ["restart"] = false, ["terminateDebuggee"] = !disconnect };

		if (suspend && Capabilities.SuspendDebuggee) arguments["suspendDebuggee"] = true;

		await SafeAsync("disconnect", arguments, cancellationToken).ConfigureAwait(false);
	}

	public Task<JsonNode?> SetBreakpointsAsync(string path, JsonArray breakpoints,
		CancellationToken cancellationToken = default) =>
		SafeAsync("setBreakpoints", new JsonObject {
			["source"] = new JsonObject { ["path"] = path, ["name"] = Path.GetFileName(path) },
			["breakpoints"] = breakpoints,
			["sourceModified"] = false,
		}, cancellationToken);

	public Task<JsonNode?> SetFunctionBreakpointsAsync(JsonArray breakpoints,
		CancellationToken cancellationToken = default) =>
		Capabilities.FunctionBreakpoints
			? SafeAsync("setFunctionBreakpoints", new JsonObject { ["breakpoints"] = breakpoints },
				cancellationToken)
			: Task.FromResult<JsonNode?>(null);

	public Task<JsonNode?> SetExceptionBreakpointsAsync(JsonArray filters,
		CancellationToken cancellationToken = default) =>
		SafeAsync("setExceptionBreakpoints", new JsonObject { ["filters"] = filters },
			cancellationToken);

	public bool IsRunning => !_disposed && _connection.IsRunning && State != DebugState.Inactive;

	private async Task<JsonNode?> SafeAsync(string command, JsonNode? arguments,
		CancellationToken cancellationToken) {
		if (!IsRunning) return null;

		try {
			return await _connection.SendAsync(command, arguments, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is DapException or OperationCanceledException) {
			Log?.Invoke($"{command} failed: {exception.Message}");
			return null;
		}
	}

	private void OnEvent(string name, JsonNode? body) {
		switch (name) {
			case "initialized":
				_ = InitializedAsync();
				break;

			case "stopped": {
				StoppedThreadId = body?["threadId"]?.GetValue<int>();
				StopReason = body?["reason"]?.GetValue<string>();
				SetState(DebugState.Stopped);
				_ = RefreshThreadsAsync();
				Stopped?.Invoke(StoppedThreadId ?? 0, StopReason ?? "pause");
				break;
			}

			case "continued":
				StoppedThreadId = null;
				StopReason = null;
				SetState(DebugState.Running);
				Continued?.Invoke();
				break;

			case "thread":
				_ = RefreshThreadsAsync();
				break;

			case "output":
				Output?.Invoke(new DebugOutput(
					body?["category"]?.GetValue<string>() ?? "console",
					body?["output"]?.GetValue<string>() ?? string.Empty,
					body?["source"]?["path"]?.GetValue<string>(),
					body?["line"]?.GetValue<int>() ?? 0));
				break;

			case "breakpoint":
				BreakpointChanged?.Invoke(body?["breakpoint"]);
				break;

			case "terminated":
			case "exited":
				Terminate(null);
				break;
		}
	}

	private JsonNode? OnReverseRequest(string command, JsonNode? arguments) {
		if (command != "runInTerminal") return null;

		string? cwd = arguments?["cwd"]?.GetValue<string>();
		string commandLine = arguments?["args"] is JsonArray args
			? string.Join(' ', args.Select(static value => Quote(value?.GetValue<string>())))
			: string.Empty;

		RunInTerminalRequested?.Invoke(commandLine, cwd);

		return new JsonObject();
	}

	private static string Quote(string? value) {
		if (string.IsNullOrEmpty(value)) return "\"\"";

		return value.Contains(' ') ? $"\"{value}\"" : value;
	}

	private static DebugCapabilities ReadCapabilities(JsonNode? body) {
		bool Flag(string name) => body?[name]?.GetValue<bool>() ?? false;

		List<ExceptionBreakpoint> filters = [];

		if (body?["exceptionBreakpointFilters"] is JsonArray array) {
			foreach (JsonNode? entry in array) {
				if (entry?["filter"]?.GetValue<string>() is not { } filter) continue;

				filters.Add(new ExceptionBreakpoint(filter,
					entry["label"]?.GetValue<string>() ?? filter,
					entry["default"]?.GetValue<bool>() ?? false));
			}
		}

		return new DebugCapabilities {
			ConfigurationDone = Flag("supportsConfigurationDoneRequest"),
			ConditionalBreakpoints = Flag("supportsConditionalBreakpoints"),
			HitConditionalBreakpoints = Flag("supportsHitConditionalBreakpoints"),
			LogPoints = Flag("supportsLogPoints"),
			FunctionBreakpoints = Flag("supportsFunctionBreakpoints"),
			DataBreakpoints = Flag("supportsDataBreakpoints"),
			InstructionBreakpoints = Flag("supportsInstructionBreakpoints"),
			StepBack = Flag("supportsStepBack"),
			StepInTargets = Flag("supportsStepInTargetsRequest"),
			RestartFrame = Flag("supportsRestartFrame"),
			RestartRequest = Flag("supportsRestartRequest"),
			TerminateRequest = Flag("supportsTerminateRequest"),
			TerminateThreads = Flag("supportsTerminateThreadsRequest"),
			SuspendDebuggee = Flag("supportSuspendDebuggee"),
			SetVariable = Flag("supportsSetVariable"),
			SetExpression = Flag("supportsSetExpression"),
			EvaluateForHovers = Flag("supportsEvaluateForHovers"),
			ExceptionInfo = Flag("supportsExceptionInfoRequest"),
			Disassemble = Flag("supportsDisassembleRequest"),
			ReadMemory = Flag("supportsReadMemoryRequest"),
			LoadedSources = Flag("supportsLoadedSourcesRequest"),
			Modules = Flag("supportsModulesRequest"),
			GotoTargets = Flag("supportsGotoTargetsRequest"),
			CompletionsRequest = Flag("supportsCompletionsRequest"),
			ClipboardContext = Flag("supportsClipboardContext"),
			ExceptionFilters = filters,
		};
	}

	private void SetState(DebugState state) {
		if (State == state) return;

		State = state;
		StateChanged?.Invoke(state);
	}

	private void Terminate(string? reason) {
		if (State == DebugState.Inactive) return;

		SetState(DebugState.Inactive);
		Terminated?.Invoke(reason);
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;
		_connection.Dispose();
	}
}
