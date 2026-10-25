using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public readonly record struct ServerCommand(string Title, string Command, JsonArray? Arguments);

public sealed class WorkspaceRequests(RoslynLanguageClient client)
{
	private int _restoreToken;
	private int _restoring;

	public event Action<string>? RestoreProgress;

	public event Action<int, string, IReadOnlyList<ServerCommand>>? Toast;

	public event Action? DiagnosticsRefreshRequested;

	public event Action? SemanticTokensRefreshRequested;

	public bool IsRestoring => Volatile.Read(ref _restoring) == 1;

	public Func<Common.Commands.WorkspaceEdit, Task<bool>>? ApplyEdit { get; set; }

	internal async Task<JsonNode?> OnApplyEditAsync(JsonNode? parameters) {
		bool applied = WorkspaceEditReader.Read(parameters?["edit"]) is { } edit && ApplyEdit is { } apply && await apply(edit).ConfigureAwait(false);

		return new JsonObject { ["applied"] = applied };
	}

	public async Task<IReadOnlyList<string>> RestorableProjectsAsync() {
		JsonNode? result = await client.RequestAsync("workspace/_roslyn_restorableProjects", new JsonObject()).ConfigureAwait(false);
		List<string> projects = [];

		foreach (JsonNode? entry in result as JsonArray ?? []) {
			if (entry?.GetValue<string>() is { } project) projects.Add(project);
		}

		return projects;
	}

	public async Task<bool> RestoreAsync(IReadOnlyList<string> projects) {
		if (Interlocked.Exchange(ref _restoring, 1) == 1) return false;

		try {
			JsonArray paths = [];

			foreach (string project in projects) paths.Add(project);

			JsonNode? result = await client.RequestAsync("workspace/_roslyn_restore", new JsonObject {
				["projectFilePaths"] = paths,
				["partialResultToken"] = $"restore/{Interlocked.Increment(ref _restoreToken)}",
			}).ConfigureAwait(false);

			return result?["success"]?.GetValue<bool>() ?? false;
		}
		finally {
			Volatile.Write(ref _restoring, 0);
		}
	}

	public void RefreshSourceGenerators(bool force) => client.Notify("workspace/_roslyn_refreshSourceGenerators", new JsonObject { ["forceRegeneration"] = force });

	internal void OnProgress(JsonNode? parameters) {
		if (parameters?["token"]?.GetValue<string>() is not { } token || !token.StartsWith("restore/", StringComparison.Ordinal)) return;

		foreach (JsonNode? item in parameters["value"] as JsonArray ?? (parameters["value"] is JsonObject single ? [single.DeepClone()] : [])) {
			string stage = item?["stage"]?.GetValue<string>() ?? string.Empty;
			string message = item?["message"]?.GetValue<string>() ?? string.Empty;

			RestoreProgress?.Invoke(stage.Length > 0 ? $"[{stage}] {message}" : message);
		}
	}

	internal void OnToast(JsonNode? parameters) {
		List<ServerCommand> commands = [];

		foreach (JsonNode? entry in parameters?["commands"] as JsonArray ?? []) {
			if (entry?["command"]?.GetValue<string>() is { } command) commands.Add(new ServerCommand(entry["title"]?.GetValue<string>() ?? command, command, entry["arguments"]?.DeepClone() as JsonArray));
		}

		Toast?.Invoke(parameters?["messageType"]?.GetValue<int>() ?? 3, parameters?["message"]?.GetValue<string>() ?? string.Empty, commands);
	}

	internal JsonNode? OnServerRequest(string method) {
		switch (method) {
			case "workspace/diagnostic/refresh":
				DiagnosticsRefreshRequested?.Invoke();
				break;

			case "workspace/semanticTokens/refresh":
				SemanticTokensRefreshRequested?.Invoke();
				break;

			case "workspace/codeLens/refresh":
				client.CodeLenses.Invalidate();
				break;

			case "workspace/projectContext/_vs_refresh":
				client.Contexts.Invalidate();
				break;
		}

		return null;
	}

	public Task<JsonNode?> ExecuteAsync(ServerCommand command) => client.RequestAsync("workspace/executeCommand", new JsonObject {
		["command"] = command.Command,
		["arguments"] = command.Arguments?.DeepClone() ?? new JsonArray(),
	});
}
