using System.Text.Json.Nodes;

using Forge.Commands;
using Forge.Common.Commands;

namespace Forge.VSCode;

public sealed class VSCodeBridge(ICommandRegistry commands, ICommandHost host) : IDisposable
{
	private readonly Dictionary<string, VSCodeExtension> _byCommand = [with(StringComparer.Ordinal)];
	private readonly List<VSCodeExtension> _activated = [];

	private NodeExtensionHost? _process;

	public IReadOnlyList<VSCodeExtension> Activated => _activated;

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

		foreach (VSCodeExtension extension in found) Register(extension);

		_ = ActivateAllAsync(found);
	}

	public Task<string?> ResolveWebviewAsync(string viewId, string? title = null) =>
		_process?.ResolveWebviewAsync(viewId, title) ?? Task.FromResult<string?>(null);

	public Task PostWebviewMessageAsync(string viewId, JsonNode? message) =>
		_process?.PostWebviewMessageAsync(viewId, message) ?? Task.CompletedTask;

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
		"window/showMessage" => ShowMessage(parameters),
		"commands/list" => CommandList(),
		_ => null,
	};

	private JsonNode? Execute(JsonNode? parameters) {
		if (parameters?["id"]?.GetValue<string>() is not { } id) return null;

		if (_byCommand.ContainsKey(id)) {
			Invoke(id);
			return null;
		}

		commands.Execute(id, host);
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
				host.Channel(parameters?["name"]?.GetValue<string>() ?? "Extensions")
					.Append((parameters?["value"]?.GetValue<string>() ?? string.Empty).TrimEnd('\n'));
				break;

			case "commands/register":
				if (parameters?["id"]?.GetValue<string>() is { } id && !_byCommand.ContainsKey(id) && _activated.Count > 0) {
					RegisterCommand(_activated[^1], new VSCodeCommand(id, Humanise(id), _activated[^1].DisplayName));
				}

				break;
		}
	}

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
