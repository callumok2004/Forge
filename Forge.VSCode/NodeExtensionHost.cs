using System.Diagnostics;
using System.Text.Json.Nodes;

using Forge.Language;

namespace Forge.VSCode;

public sealed class NodeExtensionHost : IDisposable
{
	private readonly string _storageDirectory;
	private readonly Lock _sync = new();

	private JsonRpcConnection? _connection;
	private bool _disposed;

	public NodeExtensionHost(string storageDirectory) {
		_storageDirectory = storageDirectory;
		Directory.CreateDirectory(storageDirectory);
	}

	public static string RuntimePath => Path.Combine(AppContext.BaseDirectory, "ExtensionHost", "bun.exe");

	public static string BootstrapPath => Path.Combine(AppContext.BaseDirectory, "ExtensionHost", "bootstrap.js");

	public static bool IsAvailable => File.Exists(RuntimePath) && File.Exists(BootstrapPath);

	public bool IsRunning => _connection is not null;

	public Action<string>? Log;

	public Action<string, JsonNode?>? Notification;

	public Func<string, JsonNode?, JsonNode?>? RequestHandler;

	public bool Start(HostState state) {
		lock (_sync) {
			if (_connection is not null || _disposed) return false;

			if (!IsAvailable) {
				Log?.Invoke("Extension host runtime is missing; rebuild to fetch bun.");
				return false;
			}

			ProcessStartInfo startInfo = new() {
				FileName = RuntimePath,
				WorkingDirectory = Path.GetDirectoryName(BootstrapPath),
			};

			startInfo.ArgumentList.Add("--smol");
			startInfo.ArgumentList.Add(BootstrapPath);
			startInfo.Environment["FORGE_EXTENSION_HOST"] = "1";

			try {
				JsonRpcConnection connection = new(startInfo, negotiateTransport: false);
				connection.Log += message => Log?.Invoke(message);
				connection.Notification += OnNotification;
				connection.RequestHandler = (method, parameters) => RequestHandler?.Invoke(method, parameters);

				_connection = connection;
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) {
				Log?.Invoke($"Extension host failed to start: {exception.Message}");
				return false;
			}
		}

		_ = SendAsync("host/initialize", new JsonObject { ["state"] = state.ToJson() });
		return true;
	}

	public async Task<ActivationResult?> ActivateAsync(string extensionDirectory) {
		if (_connection is not { } connection) return null;

		JsonObject parameters = new() {
			["directory"] = extensionDirectory,
			["storageDirectory"] = _storageDirectory,
		};

		JsonNode? result = await connection.SendRequestAsync("extension/activate", parameters).ConfigureAwait(false);

		return result is JsonObject payload
			? new ActivationResult(
				payload["id"]?.GetValue<string>() ?? extensionDirectory,
				Strings(payload["commands"]),
				Strings(payload["webviewProviders"]))
			: null;
	}

	public Task<JsonNode?> DeactivateAsync(string id) =>
		SendAsync("extension/deactivate", new JsonObject { ["id"] = id });

	public Task<JsonNode?> InvokeCommandAsync(string id, JsonArray? arguments = null) =>
		SendAsync("commands/invoke", new JsonObject { ["id"] = id, ["args"] = arguments ?? [] });

	public async Task<string?> ResolveWebviewAsync(string viewId, string? title = null, string? root = null) {
		JsonNode? result = await SendAsync("webview/resolve", new JsonObject {
			["viewId"] = viewId,
			["title"] = title,
			["root"] = root,
		}).ConfigureAwait(false);

		return (result as JsonObject)?["html"]?.GetValue<string>();
	}

	public Task<JsonNode?> SetWebviewVisibilityAsync(string viewId, bool visible) =>
		SendAsync("webview/visibility", new JsonObject { ["id"] = viewId, ["visible"] = visible });

	public Task<JsonNode?> PostWebviewMessageAsync(string viewId, JsonNode? message) =>
		SendAsync("webview/message", new JsonObject { ["id"] = viewId, ["message"] = message?.DeepClone() });

	public void PublishState(HostState state) => Notify("host/state", state.ToJson());

	public void RaiseEvent(string name, JsonNode? value) =>
		Notify("host/event", new JsonObject { ["name"] = name, ["value"] = value?.DeepClone() });

	public void Dispose() {
		JsonRpcConnection? connection;

		lock (_sync) {
			if (_disposed) return;

			_disposed = true;
			connection = _connection;
			_connection = null;
		}

		if (connection is null) return;

		try {
			connection.SendRequestAsync("host/shutdown", new JsonObject()).Wait(TimeSpan.FromSeconds(2));
		}
		catch (Exception exception) when (exception is AggregateException or InvalidOperationException or IOException) {
		}

		connection.Dispose();
	}

	private Task<JsonNode?> SendAsync(string method, JsonNode? parameters) =>
		_connection is { } connection ? connection.SendRequestAsync(method, parameters) : Task.FromResult<JsonNode?>(null);

	private void Notify(string method, JsonNode? parameters) => _connection?.SendNotification(method, parameters);

	private void OnNotification(string method, JsonNode? parameters) {
		if (method == "host/log") {
			Log?.Invoke(parameters?["message"]?.GetValue<string>() ?? string.Empty);
			return;
		}

		Notification?.Invoke(method, parameters);
	}

	private static string[] Strings(JsonNode? node) {
		if (node is not JsonArray array || array.Count == 0) return [];

		List<string> results = [with(array.Count)];

		foreach (JsonNode? item in array) {
			if (item?.GetValue<string>() is { Length: > 0 } text) results.Add(text);
		}

		return [.. results];
	}
}

public sealed record ActivationResult(string Id, string[] Commands, string[] WebviewProviders);
