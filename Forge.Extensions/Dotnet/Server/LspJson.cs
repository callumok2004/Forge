using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server;

internal static class LspJson
{
	public static JsonObject Position(int line, int column) => new() {
		["line"] = Math.Max(0, line - 1),
		["character"] = Math.Max(0, column - 1),
	};

	public static int Line(JsonNode? position) => (position?["line"]?.GetValue<int>() ?? 0) + 1;

	public static int Column(JsonNode? position) => (position?["character"]?.GetValue<int>() ?? 0) + 1;

	public static ServerTextEdit? Edit(JsonNode? entry) {
		if (entry?["range"]?["start"] is not { } start || entry["range"]?["end"] is not { } end) return null;

		return new ServerTextEdit(Line(start), Column(start), Line(end), Column(end), entry["newText"]?.GetValue<string>() ?? string.Empty);
	}

	public static List<ServerTextEdit> Edits(JsonNode? array) {
		List<ServerTextEdit> edits = [];

		foreach (JsonNode? entry in array as JsonArray ?? []) {
			if (Edit(entry) is { } edit) edits.Add(edit);
		}

		return edits;
	}

	public static string Markup(JsonNode? content) => content switch {
		JsonObject markup => markup["value"]?.GetValue<string>() ?? string.Empty,
		JsonValue value => value.GetValue<string>(),
		JsonArray array when array.Count > 0 => array[0]?.ToString() ?? string.Empty,
		_ => string.Empty,
	};
}
