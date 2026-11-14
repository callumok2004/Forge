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

	public static System.Text.Json.JsonElement? Property(this System.Text.Json.JsonElement element, string name) =>
		element.ValueKind == System.Text.Json.JsonValueKind.Object && element.TryGetProperty(name, out System.Text.Json.JsonElement value) && value.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) ? value : null;

	public static string? String(this System.Text.Json.JsonElement? element) => element is { ValueKind: System.Text.Json.JsonValueKind.String } value ? value.GetString() : null;

	public static int? Int(this System.Text.Json.JsonElement? element) => element is { ValueKind: System.Text.Json.JsonValueKind.Number } value && value.TryGetInt32(out int number) ? number : null;

	public static bool? Bool(this System.Text.Json.JsonElement? element) => element?.ValueKind switch {
		System.Text.Json.JsonValueKind.True => true,
		System.Text.Json.JsonValueKind.False => false,
		_ => null,
	};

	public static System.Text.Json.JsonElement? Property(this System.Text.Json.JsonElement? element, string name) => element is { } value ? value.Property(name) : null;

	public static int Line(System.Text.Json.JsonElement? position) => (position.Property("line").Int() ?? 0) + 1;

	public static int Column(System.Text.Json.JsonElement? position) => (position.Property("character").Int() ?? 0) + 1;

	public static string Markup(JsonNode? content) => content switch {
		JsonObject markup => markup["value"]?.GetValue<string>() ?? string.Empty,
		JsonValue value => value.GetValue<string>(),
		JsonArray array when array.Count > 0 => array[0]?.ToString() ?? string.Empty,
		_ => string.Empty,
	};
}
