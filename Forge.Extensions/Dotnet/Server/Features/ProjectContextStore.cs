using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class ProjectContextStore(RoslynLanguageClient client)
{
	private readonly Dictionary<string, ProjectContexts> _contexts = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	public async Task<ProjectContexts?> RequestAsync(string path) {
		JsonNode? result = await client.RequestAsync("textDocument/_vs_getProjectContexts", new JsonObject {
			["_vs_textDocument"] = new JsonObject { ["uri"] = RoslynLanguageClient.ToUri(path) },
		}).ConfigureAwait(false);

		if (result?["_vs_projectContexts"] is not JsonArray array || array.Count == 0) return null;

		List<ProjectContext> contexts = [];

		foreach (JsonNode? entry in array) {
			if (entry?["_vs_id"]?.GetValue<string>() is not { } id) continue;

			contexts.Add(new ProjectContext(id, entry["_vs_label"]?.GetValue<string>() ?? id, entry["_vs_is_miscellaneous"]?.GetValue<bool>() ?? false));
		}

		if (contexts.Count == 0) return null;

		int defaultIndex = Math.Clamp(result["_vs_defaultIndex"]?.GetValue<int>() ?? 0, 0, contexts.Count - 1);
		ProjectContexts list = new(contexts, defaultIndex);

		lock (_sync) _contexts[path] = list;

		return list;
	}

	public void Invalidate() {
		lock (_sync) _contexts.Clear();
	}

	public ProjectContexts? For(string path) {
		lock (_sync) return _contexts.GetValueOrDefault(path);
	}

	public void Select(string path, string id) {
		lock (_sync) {
			if (_contexts.TryGetValue(path, out ProjectContexts? list)) _contexts[path] = list with { SelectedId = id };
		}
	}

	internal JsonObject Identifier(string path) {
		JsonObject identifier = new() { ["uri"] = client.UriOf(path) };

		if (For(path)?.Current is { IsMiscellaneous: false } current) {
			identifier["_vs_projectContext"] = new JsonObject {
				["_vs_id"] = current.Id,
				["_vs_label"] = current.Label,
			};
		}

		return identifier;
	}
}
