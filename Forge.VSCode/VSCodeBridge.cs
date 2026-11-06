using System.Text.Json.Nodes;

using Forge.Common.Commands;
using Forge.Common.Render;

namespace Forge.VSCode;

public sealed class VSCodeBridge(ICommandRegistry commands, ICommandHost host) : IDisposable
{
	private readonly Dictionary<string, VSCodeExtension> _byCommand = [with(StringComparer.Ordinal)];
	private readonly List<VSCodeExtension> _activated = [];

	private readonly List<WebviewViewEntry> _views = [];
	private readonly Dictionary<string, string> _context = new(StringComparer.Ordinal);
	private readonly Dictionary<string, VSCodeCommand> _commandInfo = new(StringComparer.Ordinal);
	private readonly List<VSCodeMenuItem> _editorTitle = [];

	public sealed record EditorAction(string Command, string Title, string? Icon);

	public IReadOnlyList<EditorAction> EditorTitleActions {
		get {
			List<EditorAction> actions = [];

			foreach (VSCodeMenuItem item in _editorTitle) {
				if (item.Group is { } group && !group.StartsWith("navigation", StringComparison.Ordinal)) continue;

				if (!WhenExpression.Evaluate(item.When, _context)) continue;

				_commandInfo.TryGetValue(item.Command, out VSCodeCommand? info);
				actions.Add(new EditorAction(item.Command, info?.Title ?? item.Command, info?.Icon));
			}

			return actions;
		}
	}

	public Action? ContextChanged { get; set; }

	public Func<string, JsonNode?, Task<JsonNode?>?>? WorkbenchRequest { get; set; }

	public Action<string, JsonNode?>? WorkbenchNotification { get; set; }

	public Task<JsonNode?> RequestAsync(string method, JsonNode? parameters) => _process?.RequestAsync(method, parameters) ?? Task.FromResult<JsonNode?>(null);

	public void Notify(string method, JsonNode? parameters) => _process?.NotifyHost(method, parameters);

	public void SetHostContext(string key, string value) {
		if (_context.TryGetValue(key, out string? existing) && string.Equals(existing, value, StringComparison.Ordinal)) return;

		_context[key] = value;
		ContextChanged?.Invoke();
	}

	private NodeExtensionHost? _process;

	public IReadOnlyList<VSCodeExtension> Activated => _activated;

	public IReadOnlyList<WebviewViewEntry> WebviewViews {
		get {
			List<WebviewViewEntry> visible = [];

			foreach (WebviewViewEntry entry in _views) {
				if (WhenExpression.Evaluate(entry.When, _context)) visible.Add(entry);
			}

			return visible;
		}
	}

	public Action? ViewsChanged { get; set; }

	public Action<string, string>? WebviewHtmlChanged { get; set; }

	public Action<string, string>? WebviewMessage { get; set; }

	public bool IsRunning => _process?.IsRunning ?? false;

	public void Start(string storageDirectory, HostState state, params string[] searchDirectories) {
		List<VSCodeExtension> found = VSCodeExtensions.Discover(searchDirectories);

		if (found.Count == 0) return;

		if (!NodeExtensionHost.IsAvailable) {
			Log("Extension host runtime is missing; rebuild Forge to fetch it.");
			return;
		}

		NodeExtensionHost process = new(storageDirectory);
		process.Log = Log;
		process.Notification = OnNotification;
		process.RequestHandler = OnRequest;
		process.AsyncRequestHandler = OnDeferredRequest;

		if (!process.Start(state)) {
			process.Dispose();
			return;
		}

		_process = process;

		foreach (VSCodeExtension extension in found) {
			Register(extension);
			RegisterViews(extension);
		}

		if (_views.Count > 0) ViewsChanged?.Invoke();

		_ = ActivateAllAsync(found);
	}

	public Task<string?> ResolveWebviewAsync(string viewId, string? title = null, string? root = null) =>
		_process?.ResolveWebviewAsync(viewId, title, root) ?? Task.FromResult<string?>(null);

	public bool TraceWebviews { get; set; }

	public Task SetWebviewVisibilityAsync(string viewId, bool visible) =>
		_process?.SetWebviewVisibilityAsync(viewId, visible) ?? Task.CompletedTask;

	public Task PostWebviewMessageAsync(string viewId, JsonNode? message) {
		if (TraceWebviews) Log($"webview -> extension [{viewId}] {Summarise(message)}");

		return _process?.PostWebviewMessageAsync(viewId, message) ?? Task.CompletedTask;
	}

	public void PublishState(HostState state) => _process?.PublishState(state);

	public void RaiseEvent(string name, JsonNode? value) => _process?.RaiseEvent(name, value);

	public void Dispose() {
		_process?.Dispose();
		_process = null;
	}

	private async Task ActivateAllAsync(List<VSCodeExtension> found) {
		foreach (VSCodeExtension extension in found) {
			try {
				ActivationResult? result = await (_process?.ActivateAsync(extension.Directory) ?? Task.FromResult<ActivationResult?>(null))
					.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

				if (result is null) continue;

				_activated.Add(extension);

				foreach (string id in result.Commands) {
					if (_byCommand.ContainsKey(id)) continue;

					RegisterCommand(extension, new VSCodeCommand(id, Humanise(id), extension.DisplayName));
				}

				Log($"{extension.DisplayName}: {result.Commands.Length} commands, {result.WebviewProviders.Length} views.");
			}
			catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or IOException) {
				Log($"{extension.DisplayName} failed to activate: {exception.Message}");
			}
		}
	}

	private void RegisterViews(VSCodeExtension extension) {
		foreach (VSCodeViewContainer container in extension.Containers) {
			foreach (VSCodeWebviewView view in container.WebviewViews) {
				string? when = container.When is { Length: > 0 } outer
					? view.When is { Length: > 0 } inner ? $"({outer}) && ({inner})" : outer
					: view.When;

				string? icon = container.Icon is { Length: > 0 } relative && !relative.StartsWith("$(", StringComparison.Ordinal) ? Path.GetFullPath(Path.Combine(extension.Directory, relative)) : null;

				_views.Add(new WebviewViewEntry(view.Id, view.Name is { Length: > 0 } name ? name : container.Title, extension.Directory, extension.Identifier, when, container.Location, icon));
			}
		}
	}

	private void Register(VSCodeExtension extension) {
		foreach (VSCodeCommand command in extension.Commands) {
			_commandInfo.TryAdd(command.Id, command);
			RegisterCommand(extension, command);
		}

		_editorTitle.AddRange(extension.EditorTitle);
	}

	private void RegisterCommand(VSCodeExtension extension, VSCodeCommand command) {
		if (!_byCommand.TryAdd(command.Id, extension)) return;

		commands.Register(new Command(
			command.Id,
			command.Title,
			command.Category ?? extension.DisplayName,
			_ => Invoke(command.Id)));
	}

	private void Invoke(string id) {
		if (_process is not { } process) return;

		_ = process.InvokeCommandAsync(id).ContinueWith(
			task => {
				if (task.Exception?.GetBaseException() is { } error) Log($"{id} failed: {error.Message}");
			},
			TaskScheduler.Default);
	}

	private JsonNode? OnRequest(string method, JsonNode? parameters) => method switch {
		"commands/execute" => Execute(parameters),
		"webview/postMessage" => WebviewSend(parameters),
		"commands/list" => CommandList(),
		_ => Unhandled(method, parameters),
	};

	private Task<JsonNode?>? OnDeferredRequest(string method, JsonNode? parameters) => method switch {
		"window/showMessage" => ShowMessageAsync(parameters),
		"window/showQuickPick" => ShowQuickPickAsync(parameters),
		_ => WorkbenchRequest?.Invoke(method, parameters),
	};

	private Task<JsonNode?> ShowMessageAsync(JsonNode? parameters) {
		string message = parameters?["message"]?.GetValue<string>() ?? string.Empty;
		JsonArray items = parameters?["items"] as JsonArray ?? [];
		MessageSeverity severity = parameters?["level"]?.GetValue<string>() switch {
			"error" => MessageSeverity.Error,
			"warning" => MessageSeverity.Warning,
			_ => MessageSeverity.Info,
		};

		List<string> titles = [with(items.Count)];

		foreach (JsonNode? item in items) titles.Add(item is JsonObject value ? value["title"]?.GetValue<string>() ?? string.Empty : item?.GetValue<string>() ?? string.Empty);

		TaskCompletionSource<JsonNode?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

		host.ShowMessage(severity, message, titles, index => answer.TrySetResult(index >= 0 ? items[index]?.DeepClone() : null));

		if (titles.Count == 0) answer.TrySetResult(null);

		return answer.Task;
	}

	private Task<JsonNode?> ShowQuickPickAsync(JsonNode? parameters) {
		JsonArray items = parameters?["items"] as JsonArray ?? [];
		string placeholder = parameters?["options"]?["placeHolder"]?.GetValue<string>() ?? parameters?["options"]?["title"]?.GetValue<string>() ?? string.Empty;
		List<QuickPickItem> entries = [with(items.Count)];

		foreach (JsonNode? item in items) {
			entries.Add(item is JsonObject value
				? new QuickPickItem(value["label"]?.GetValue<string>() ?? string.Empty, value["description"]?.GetValue<string>() ?? string.Empty, value["detail"]?.GetValue<string>() ?? string.Empty)
				: new QuickPickItem(item?.GetValue<string>() ?? string.Empty));
		}

		TaskCompletionSource<JsonNode?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

		host.ShowQuickPick(placeholder, entries, index => answer.TrySetResult(index >= 0 ? items[index]?.DeepClone() : null));

		return answer.Task;
	}

	private JsonNode? Unhandled(string method, JsonNode? parameters) {
		Log($"Unhandled API call: {method} {Summarise(parameters)}");
		return null;
	}

	private static string Summarise(JsonNode? parameters) {
		string text = parameters?.ToJsonString() ?? string.Empty;

		return text.Length > 300 ? text[..300] + "..." : text;
	}

	private JsonNode? Execute(JsonNode? parameters) {
		if (parameters?["id"]?.GetValue<string>() is not { } id) return null;

		if (id == "setContext") {
			SetContext(parameters["args"] as JsonArray);
			return null;
		}

		if (_byCommand.ContainsKey(id)) {
			Invoke(id);
			return null;
		}

		commands.Execute(id, host);
		return null;
	}

	private JsonNode? WebviewSend(JsonNode? parameters) {
		if (parameters?["id"]?.GetValue<string>() is not { } id) return null;

		if (TraceWebviews) Log($"extension -> webview [{id}] {Summarise(parameters["message"])}");

		WebviewMessage?.Invoke(id, parameters["message"]?.ToJsonString() ?? "null");
		return null;
	}

	private void SetContext(JsonArray? arguments) {
		if (arguments is not { Count: >= 2 } || arguments[0]?.GetValue<string>() is not { } key) return;

		string value = arguments[1] switch {
			JsonValue node when node.TryGetValue(out bool flag) => flag ? "true" : "false",
			JsonValue node when node.TryGetValue(out string? text) => text ?? string.Empty,
			null => string.Empty,
			JsonNode node => node.ToJsonString(),
		};

		if (_context.TryGetValue(key, out string? existing) &&
			string.Equals(existing, value, StringComparison.Ordinal)) {
			return;
		}

		_context[key] = value;
		ViewsChanged?.Invoke();
		ContextChanged?.Invoke();
	}

	private JsonNode? CommandList() {
		JsonArray list = [];

		foreach (ICommand command in commands.All()) list.Add(command.GetId());

		return list;
	}

	private void OnNotification(string method, JsonNode? parameters) {
		switch (method) {
			case "output/append":
				host.Channel(parameters?["name"]?.GetValue<string>() ?? "Extensions")
					.Append((parameters?["value"]?.GetValue<string>() ?? string.Empty).TrimEnd('\n'));
				break;

			case "host/log":
				Log(parameters?["message"]?.GetValue<string>() ?? string.Empty);
				break;

			case "webview/setHtml":
				if (parameters?["id"]?.GetValue<string>() is { } view) {
					WebviewHtmlChanged?.Invoke(view, parameters["html"]?.GetValue<string>() ?? string.Empty);
				}

				break;

			case "window/progress":
				Progress(parameters);
				break;

			default:
				if (WorkbenchNotification is { } workbench) {
					workbench(method, parameters);
					break;
				}

				if (method is not ("webview/registerProvider" or "webview/unregisterProvider"
					or "commands/unregister" or "webview/show" or "host/ready")) {
					Log($"Unhandled API notification: {method} {Summarise(parameters)}");
				}

				break;

			case "commands/register":
				if (parameters?["id"]?.GetValue<string>() is { } id && !_byCommand.ContainsKey(id) && _activated.Count > 0) {
					RegisterCommand(_activated[^1], new VSCodeCommand(id, Humanise(id), _activated[^1].DisplayName));
				}

				break;
		}
	}

	private void Progress(JsonNode? parameters) {
		string? title = parameters?["title"]?.GetValue<string>();
		bool running = parameters?["state"]?.GetValue<string>() is not "end";

		if (!IsClaudeIdentifier(title)) return;

		if (running) Ambient.Begin(AmbientSignal.Task);
		else Ambient.End(AmbientSignal.Task);
	}

	private static bool IsClaudeIdentifier(string? value) =>
		value is not null && value.Contains("claude", StringComparison.OrdinalIgnoreCase);

	public void Log(string line) => host.Channel("Extensions").Append(line);

	private static string Humanise(string id) {
		int dot = id.LastIndexOf('.');
		ReadOnlySpan<char> tail = dot >= 0 ? id.AsSpan(dot + 1) : id.AsSpan();

		Span<char> buffer = stackalloc char[tail.Length * 2];
		int written = 0;

		for (int i = 0; i < tail.Length; i++) {
			if (i > 0 && char.IsUpper(tail[i]) && !char.IsUpper(tail[i - 1])) buffer[written++] = ' ';

			buffer[written++] = i == 0 ? char.ToUpperInvariant(tail[i]) : tail[i];
		}

		return new string(buffer[..written]);
	}
}
