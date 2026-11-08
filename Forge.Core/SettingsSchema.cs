using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core;

public sealed record SettingDefinition(
	string Key,
	string Category,
	string Section,
	string[] Types,
	JsonNode? Default,
	string Description,
	string[]? Enum,
	string[]? EnumDescriptions,
	double? Minimum,
	double? Maximum,
	string? Deprecation,
	int Order)
{
	public bool IsBoolean => Types is ["boolean"];

	public bool IsNumber => Types.Length > 0 && Types[0] is "number" or "integer";

	public bool IsInteger => Types.Length > 0 && Types[0] == "integer";

	public bool IsString => Types.Length > 0 && Types[0] == "string" && Enum is null;

	public bool IsEnum => Enum is { Length: > 0 };

	public bool IsComplex => !IsBoolean && !IsNumber && !IsString && !IsEnum;
}

public static class SettingsSchema
{
	public const string ExtensionsCategory = "Extensions";

	private static readonly List<SettingDefinition> _all = [];
	private static readonly Dictionary<string, SettingDefinition> _byKey = new(StringComparer.Ordinal);
	private static bool _loaded;

	public static IReadOnlyList<SettingDefinition> All {
		get {
			EnsureLoaded();
			return _all;
		}
	}

	public static SettingDefinition? Find(string key) {
		EnsureLoaded();
		return _byKey.GetValueOrDefault(key);
	}

	public static IEnumerable<string> ExtensionDirectories { get; set; } = [];

	public static void Invalidate() {
		_loaded = false;
		_all.Clear();
		_byKey.Clear();
	}

	private static void EnsureLoaded() {
		if (_loaded) return;

		_loaded = true;

		string core = Path.Combine(AppContext.BaseDirectory, "core-settings.json");

		if (Parse(core) is JsonArray sections) {
			foreach (JsonNode? section in sections) {
				if (section is JsonObject entry) AddSection(entry, entry["category"]?.GetValue<string>() ?? "Text Editor", entry["title"]?.GetValue<string>() ?? string.Empty);
			}
		}

		foreach (string root in ExtensionDirectories) {
			if (!Directory.Exists(root)) continue;

			IEnumerable<string> directories;

			try {
				directories = Directory.EnumerateDirectories(root).ToArray();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				continue;
			}

			foreach (string directory in directories) AddManifest(directory);
		}
	}

	private static void AddManifest(string directory) {
		if (Parse(Path.Combine(directory, "package.json")) is not JsonObject manifest) return;

		JsonNode? contributed = manifest["contributes"]?["configuration"];

		if (contributed is null) return;

		JsonObject? nls = Parse(Path.Combine(directory, "package.nls.json")) as JsonObject;
		string name = Localize(manifest["displayName"]?.GetValue<string>(), nls) ?? manifest["name"]?.GetValue<string>() ?? Path.GetFileName(directory);

		if (contributed is JsonArray array) {
			foreach (JsonNode? section in array) {
				if (section is JsonObject entry) AddSection(entry, ExtensionsCategory, Localize(entry["title"]?.GetValue<string>(), nls) ?? name, nls);
			}
		}
		else if (contributed is JsonObject single) {
			AddSection(single, ExtensionsCategory, Localize(single["title"]?.GetValue<string>(), nls) ?? name, nls);
		}
	}

	private static void AddSection(JsonObject section, string category, string title, JsonObject? nls = null) {
		if (section["properties"] is not JsonObject properties) return;

		foreach ((string key, JsonNode? node) in properties) {
			if (node is not JsonObject schema || _byKey.ContainsKey(key)) continue;

			string description = Localize(Text(schema, "markdownDescription") ?? Text(schema, "description"), nls) ?? string.Empty;

			SettingDefinition definition = new(
				key,
				category,
				title,
				Types(schema["type"]),
				schema["default"]?.DeepClone(),
				description,
				Strings(schema["enum"]),
				Strings(schema["markdownEnumDescriptions"] ?? schema["enumDescriptions"], nls),
				Number(schema["minimum"]),
				Number(schema["maximum"]),
				Localize(Text(schema, "markdownDeprecationMessage") ?? Text(schema, "deprecationMessage"), nls),
				schema["order"] is JsonValue order && order.TryGetValue(out double value) ? (int)value : int.MaxValue);

			_all.Add(definition);
			_byKey[key] = definition;
		}
	}

	private static string? Text(JsonObject schema, string name) => schema[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

	private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double number) ? number : null;

	private static string[] Types(JsonNode? node) {
		if (node is JsonValue value && value.TryGetValue(out string? single)) return [single];

		if (node is JsonArray array) return [.. array.Select(static item => item?.GetValue<string>() ?? "null").Where(static type => type != "null")];

		return [];
	}

	private static string[]? Strings(JsonNode? node, JsonObject? nls = null) {
		if (node is not JsonArray array) return null;

		string[] values = new string[array.Count];

		for (int i = 0; i < array.Count; i++) values[i] = Localize(array[i] is JsonValue value ? value.ToString() : array[i]?.ToJsonString(), nls) ?? string.Empty;

		return values;
	}

	private static string? Localize(string? text, JsonObject? nls) {
		if (text is not ['%', .., '%'] || nls is null) return text;

		return nls[text[1..^1]] switch {
			JsonValue value when value.TryGetValue(out string? localized) => localized,
			JsonObject message when message["message"] is JsonValue inner && inner.TryGetValue(out string? localized) => localized,
			_ => text,
		};
	}

	private static JsonNode? Parse(string path) {
		try {
			if (!File.Exists(path)) return null;

			return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
			return null;
		}
	}
}
