using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public readonly record struct ServerCodeAction(int Index, string Title, string Kind, bool IsPreferred, string? DisabledReason);

public sealed class CodeActionRequests(RoslynLanguageClient client)
{
	private JsonNode?[] _actions = [];

	public async Task<IReadOnlyList<ServerCodeAction>> RequestAsync(string path, int startLine, int startColumn, int endLine, int endColumn, bool automatic) {
		JsonNode? result = await client.RequestAsync("textDocument/codeAction", new JsonObject {
			["textDocument"] = client.Contexts.Identifier(path),
			["range"] = new JsonObject {
				["start"] = LspJson.Position(startLine, startColumn),
				["end"] = LspJson.Position(endLine, endColumn),
			},
			["context"] = new JsonObject {
				["diagnostics"] = client.Diagnostics.Overlapping(path, startLine, endLine),
				["triggerKind"] = automatic ? 2 : 1,
			},
		}).ConfigureAwait(false);

		JsonArray items = result as JsonArray ?? [];
		List<ServerCodeAction> actions = [with(items.Count)];

		_actions = [.. items];

		for (int i = 0; i < items.Count; i++) {
			JsonNode? item = items[i];

			if (item?["title"]?.GetValue<string>() is not { } title) continue;

			actions.Add(new ServerCodeAction(i, title, item["kind"]?.GetValue<string>() ?? string.Empty, item["isPreferred"]?.GetValue<bool>() ?? false, item["disabled"]?["reason"]?.GetValue<string>()));
		}

		return actions;
	}

	public JsonNode? Action(int index) => (uint)index < (uint)_actions.Length ? _actions[index]?.DeepClone() : null;

	public Task<JsonNode?> ResolveAsync(JsonNode action) => client.RequestAsync("codeAction/resolve", action.AsObject());

	public Task<JsonNode?> ResolveFixAllAsync(string title, string scope, JsonNode data) => client.RequestAsync("codeAction/resolveFixAll", new JsonObject {
		["title"] = title,
		["scope"] = scope,
		["data"] = data.DeepClone(),
	});
}
