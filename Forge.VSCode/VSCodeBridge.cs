using System.Text.Json.Nodes;

using Forge.Commands;
using Forge.Common.Commands;
using Forge.Common.Render;

namespace Forge.VSCode;

public sealed class VSCodeBridge(ICommandRegistry commands, ICommandHost host) : IDisposable
{
	private readonly Dictionary<string, VSCodeExtension> _byCommand = [with(StringComparer.Ordinal)];
	private readonly List<VSCodeExtension> _activated = [];

	private readonly List<WebviewViewEntry> _views = [];
	private readonly Dictionary<string, string> _context = new(StringComparer.Ordinal);

	private NodeExtensionHost? _process;
	private VSCodeExtension? _claude;

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

		if (!process.Start(state)) {
			process.Dispose();
			return;
		}

		_process = process;

		foreach (VSCodeExtension extension in found) {
			if (IsClaudeIdentifier(extension.Identifier)) _claude = extension;

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

				_views.Add(new WebviewViewEntry(view.Id,
					view.Name is { Length: > 0 } name ? name : container.Title,
					extension.Directory, extension.Identifier, when, container.Location));
			}
		}
	}

	private void Register(VSCodeExtension extension) {
		foreach (VSCodeCommand command in extension.Commands) RegisterCommand(extension, command);
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
		"workspace/openTextDocument" => OpenTextDocument(parameters),
		"window/showTextDocument" => ShowTextDocument(parameters),
		"window/showMessage" => ShowMessage(parameters),
		"commands/list" => CommandList(),
		"window/showQuickPick" => null,
		_ => Unhandled(method, parameters),
	};

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

		if (OwnedByClaude(id)) Ambient.Pulse(AmbientSignal.Claude, 0.9f);

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
	}

	private JsonNode? OpenTextDocument(JsonNode? parameters) {
		if (parameters?["target"]?.GetValue<string>() is { Length: > 0 } path && File.Exists(path)) {
			host.OpenFile(path);
			return HostEditor.UriFor(path);
		}

		if (parameters?["content"]?.GetValue<string>() is not { } content) return null;

		string name = parameters["fileName"]?.GetValue<string>() is { Length: > 0 } given
			? Path.GetFileName(given)
			: "Untitled";

		string scratch = Path.Combine(Path.GetTempPath(), "Forge", "untitled");

		try {
			Directory.CreateDirectory(scratch);

			string file = Path.Combine(scratch, $"{Path.GetFileNameWithoutExtension(name)}-{Environment.TickCount}{Path.GetExtension(name)}");

			File.WriteAllText(file, content);
			host.OpenFile(file);

			return HostEditor.UriFor(file);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			Log($"Could not open the document: {exception.Message}");
			return null;
		}
	}

	private JsonNode? ShowTextDocument(JsonNode? parameters) {
		if (parameters?["uri"]?.GetValue<string>() is not { Length: > 0 } uri) return null;

		string path = uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
			? new Uri(uri).LocalPath
			: uri;

		if (File.Exists(path)) host.OpenFile(path);

		return null;
	}

	private JsonNode? ShowMessage(JsonNode? parameters) {
		Log(parameters?["message"]?.GetValue<string>() ?? string.Empty);
		return null;
	}

	private JsonNode? CommandList() {
		JsonArray list = [];

		foreach (ICommand command in commands.All()) list.Add(command.GetId());

		return list;
	}

	private void OnNotification(string method, JsonNode? parameters) {
		switch (method) {
			case "output/append":
				if (IsClaudeIdentifier(parameters?["name"]?.GetValue<string>())) {
					Ambient.Pulse(AmbientSignal.Claude, 0.5f);
				}

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

		if (!IsClaudeIdentifier(title)) {
			if (running) Ambient.Pulse(AmbientSignal.Notification, 0.6f);
			return;
		}

		if (running) {
			Ambient.Begin(AmbientSignal.Task);
			Ambient.Begin(AmbientSignal.Claude);
			return;
		}

		Ambient.End(AmbientSignal.Task);
		Ambient.End(AmbientSignal.Claude);
	}

	private bool OwnedByClaude(string id) =>
		_claude is not null && _byCommand.TryGetValue(id, out VSCodeExtension? owner) && ReferenceEquals(owner, _claude);

	private static bool IsClaudeIdentifier(string? value) =>
		value is not null && value.Contains("claude", StringComparison.OrdinalIgnoreCase);

	private void Log(string line) => host.Channel("Extensions").Append(line);

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
