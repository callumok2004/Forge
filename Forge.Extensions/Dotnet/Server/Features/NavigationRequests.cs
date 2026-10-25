using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class NavigationRequests(RoslynLanguageClient client)
{
	public Task<IReadOnlyList<ServerLocation>> ReferencesAsync(string path, int line, int column, bool includeDeclaration = true) => LocationsAsync("textDocument/references", path, line, column, new JsonObject { ["includeDeclaration"] = includeDeclaration });

	public Task<IReadOnlyList<ServerLocation>> ImplementationsAsync(string path, int line, int column) => LocationsAsync("textDocument/implementation", path, line, column);

	public Task<IReadOnlyList<ServerLocation>> DefinitionsAsync(string path, int line, int column) => LocationsAsync("textDocument/definition", path, line, column);

	public Task<IReadOnlyList<ServerLocation>> TypeDefinitionsAsync(string path, int line, int column) => LocationsAsync("textDocument/typeDefinition", path, line, column);

	private async Task<IReadOnlyList<ServerLocation>> LocationsAsync(string method, string path, int line, int column, JsonObject? context = null) {
		JsonObject parameters = client.DocumentAt(path, line, column);

		if (context is not null) parameters["context"] = context;

		JsonNode? result = await client.RequestAsync(method, parameters).ConfigureAwait(false);
		List<ServerLocation> locations = [];
		Dictionary<string, string[]> lines = new(StringComparer.OrdinalIgnoreCase);

		foreach (JsonNode? entry in result as JsonArray ?? (result is null ? [] : [result])) {
			string? uri = entry?["uri"]?.GetValue<string>() ?? entry?["targetUri"]?.GetValue<string>();
			JsonNode? range = entry?["range"] ?? entry?["targetSelectionRange"] ?? entry?["targetRange"];

			if (uri is null || range?["start"] is not { } start || await client.PathOfAsync(uri).ConfigureAwait(false) is not { } file) continue;

			JsonNode? end = range["end"] ?? start;
			int targetLine = LspJson.Line(start);

			if (!lines.TryGetValue(file, out string[]? text)) lines[file] = text = ReadLines(file);

			locations.Add(new ServerLocation(file, targetLine, LspJson.Column(start), LspJson.Line(end), LspJson.Column(end), (uint)(targetLine - 1) < (uint)text.Length ? text[targetLine - 1] : string.Empty));
		}

		return locations;
	}

	public async Task<IReadOnlyList<ServerSymbol>> DocumentSymbolsAsync(string path) {
		JsonNode? result = await client.RequestAsync("textDocument/documentSymbol", new JsonObject {
			["textDocument"] = client.Contexts.Identifier(path),
		}).ConfigureAwait(false);

		List<ServerSymbol> symbols = [];
		Collect(result as JsonArray, 0, symbols);
		return symbols;

		static void Collect(JsonArray? array, int depth, List<ServerSymbol> into) {
			foreach (JsonNode? entry in array ?? []) {
				if (entry?["name"]?.GetValue<string>() is not { } name) continue;

				JsonNode? start = (entry["selectionRange"] ?? entry["range"] ?? entry["location"]?["range"])?["start"];

				into.Add(new ServerSymbol(name, entry["kind"]?.GetValue<int>() ?? 0, depth, LspJson.Line(start), LspJson.Column(start)));
				Collect(entry["children"] as JsonArray, depth + 1, into);
			}
		}
	}

	private static string[] ReadLines(string path) {
		try {
			return File.ReadAllLines(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return [];
		}
	}
}
