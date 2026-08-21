using System.Text.Json;

namespace Forge.Syntax;

public sealed record GrammarEntry(
	string Id,
	string Name,
	string ScopeName,
	string File,
	string[] Extensions,
	string[] FileNames,
	string? LineComment,
	string? BlockCommentStart,
	string? BlockCommentEnd);

public static class GrammarIndex
{
	private static readonly string[] NoStrings = [];
	private static readonly Lock Sync = new();

	private static Dictionary<string, GrammarEntry>? _byExtension;
	private static Dictionary<string, GrammarEntry>? _byId;

	public static string Directory => Path.Combine(AppContext.BaseDirectory, "Grammars");

	public static GrammarEntry? ForPath(string? path) {
		if (string.IsNullOrEmpty(path)) return null;

		Load();

		string fileName = Path.GetFileName(path);

		if (_byExtension!.TryGetValue(fileName, out GrammarEntry? byName)) return byName;

		string extension = Path.GetExtension(fileName);

		return extension.Length > 0 && _byExtension.TryGetValue(extension, out GrammarEntry? found) ? found : null;
	}

	public static GrammarEntry? ForId(string id) {
		Load();

		return _byId!.GetValueOrDefault(id);
	}

	public static IEnumerable<GrammarEntry> All() {
		Load();

		return _byId!.Values;
	}

	private static void Load() {
		if (_byExtension is not null) return;

		lock (Sync) {
			if (_byExtension is not null) return;

			Dictionary<string, GrammarEntry> byExtension = [with(StringComparer.OrdinalIgnoreCase)];
			Dictionary<string, GrammarEntry> byId = [with(StringComparer.OrdinalIgnoreCase)];

			foreach (GrammarEntry entry in Read()) {
				byId[entry.Id] = entry;

				foreach (string extension in entry.Extensions) byExtension.TryAdd(extension, entry);

				foreach (string name in entry.FileNames) byExtension.TryAdd(name, entry);
			}

			_byId = byId;
			_byExtension = byExtension;
		}
	}

	private static List<GrammarEntry> Read() {
		List<GrammarEntry> entries = [];

		try {
			string path = Path.Combine(Directory, "grammars.json");

			if (!File.Exists(path)) return entries;

			using FileStream stream = File.OpenRead(path);
			using JsonDocument document = JsonDocument.Parse(stream);

			foreach (JsonElement element in document.RootElement.EnumerateArray()) {
				if (Text(element, "id") is not { } id || Text(element, "file") is not { } file) continue;

				entries.Add(new GrammarEntry(
					id,
					Text(element, "name") ?? id,
					Text(element, "scopeName") ?? string.Empty,
					file,
					Strings(element, "extensions"),
					Strings(element, "filenames"),
					Text(element, "lineComment"),
					Text(element, "blockCommentStart"),
					Text(element, "blockCommentEnd")));
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
		}

		return entries;
	}

	private static string? Text(in JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static string[] Strings(in JsonElement element, string name) {
		if (!element.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array) return NoStrings;

		int count = array.GetArrayLength();

		if (count == 0) return NoStrings;

		string[] results = new string[count];
		int written = 0;

		foreach (JsonElement item in array.EnumerateArray()) {
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) results[written++] = text;
		}

		return written == count ? results : results[..written];
	}
}
