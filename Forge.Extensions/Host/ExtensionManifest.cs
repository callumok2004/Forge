using System.Text.Json.Nodes;
using System.Text.Json;

using Forge.Common.Extensions;

namespace Forge.Extensions.Host;

public sealed class ExtensionManifest : IExtension
{
	private static readonly string[] NoStrings = [];

	private ExtensionManifest(string directory, string identifier, string name, string? publisher,
		string? displayName, string? description, string? version, string? main) {
		Directory = directory;
		Identifier = identifier;
		Name = name;
		Publisher = publisher;
		DisplayName = displayName;
		Description = description;
		Version = version;
		Main = main;
	}

	public string Directory { get; }

	public string Identifier { get; }

	public string Name { get; }

	public string? Publisher { get; }

	public string? DisplayName { get; }

	public string? Description { get; }

	public string? Version { get; }

	public string? Main { get; }

	public bool RequiresHost() => Main is { Length: > 0 };

	public string GetDirectory() => Directory;

	public string GetIdentifier() => Identifier;

	public string GetName() => Name;

	public string? GetPublisher() => Publisher;

	public string? GetDisplayName() => DisplayName;

	public string? GetDescription() => Description;

	public string? GetVersion() => Version;

	public string? GetMain() => Main;

	public LanguageContribution[] GetLanguages() => Languages;

	public GrammarContribution[] GetGrammars() => Grammars;

	public ThemeContribution[] GetThemes() => Themes;

	public CommandContribution[] GetCommands() => Commands;

	public SnippetContribution[] GetSnippets() => Snippets;

	public LanguageContribution[] Languages { get; private set; } = [];

	public GrammarContribution[] Grammars { get; private set; } = [];

	public ThemeContribution[] Themes { get; private set; } = [];

	public CommandContribution[] Commands { get; private set; } = [];

	public SnippetContribution[] Snippets { get; private set; } = [];

	public static ExtensionManifest? Load(string directory) {
		string path = Path.Combine(directory, "package.json");

		try {
			if (!File.Exists(path)) return null;

			JsonDocumentOptions options = new() {
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			};

			if (JsonNode.Parse(File.ReadAllText(path), documentOptions: options) is not JsonObject root) return null;

			string? name = Text(root, "name");

			if (name is null) return null;

			string? publisher = Text(root, "publisher");

			ExtensionManifest manifest = new(
				directory,
				publisher is null ? name : $"{publisher}.{name}",
				name,
				publisher,
				Text(root, "displayName"),
				Text(root, "description"),
				Text(root, "version"),
				Text(root, "main"));

			if (root["contributes"] is JsonObject contributes) manifest.ReadContributions(contributes);

			return manifest;
		}
		catch (Exception exception) when (exception is IOException or JsonException
			or UnauthorizedAccessException) {
			return null;
		}
	}

	private void ReadContributions(JsonObject contributes) {
		Languages = ReadArray(contributes, "languages", entry =>
			Text(entry, "id") is { } id
				? new LanguageContribution(
					id,
					Strings(entry, "extensions", lowercase: true),
					Strings(entry, "filenames"),
					Strings(entry, "aliases"),
					Text(entry, "configuration") is { } configuration
						? Path.Combine(Directory, configuration)
						: null)
				: null);

		Grammars = ReadArray(contributes, "grammars", entry =>
			Text(entry, "scopeName") is { } scope && Text(entry, "path") is { } path
				? new GrammarContribution(Text(entry, "language"), scope, Path.Combine(Directory, path))
				: null);

		Themes = ReadArray(contributes, "themes", entry =>
			Text(entry, "path") is { } path
				? new ThemeContribution(
					Text(entry, "label") ?? Path.GetFileNameWithoutExtension(path),
					Text(entry, "id"),
					Text(entry, "uiTheme") is not "vs",
					Path.Combine(Directory, path))
				: null);

		Commands = ReadArray(contributes, "commands", entry =>
			Text(entry, "command") is { } command && Text(entry, "title") is { } title
				? new CommandContribution(command, title, Text(entry, "category"),
					entry["icon"] is JsonValue icon ? icon.ToString() : null)
				: null);

		Snippets = ReadArray(contributes, "snippets", entry =>
			Text(entry, "language") is { } language && Text(entry, "path") is { } path
				? new SnippetContribution(language, Path.Combine(Directory, path))
				: null);
	}

	private static T[] ReadArray<T>(JsonObject contributes, string key, Func<JsonObject, T?> read)
		where T : class {
		if (contributes[key] is not JsonArray array || array.Count == 0) return [];

		List<T> results = [with(array.Count)];

		foreach (JsonNode? node in array) {
			if (node is JsonObject entry && read(entry) is { } value) results.Add(value);
		}

		return results.Count == 0 ? [] : [.. results];
	}

	private static string? Text(JsonObject node, string key) =>
		node[key] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0
			? text
			: null;

	private static string[] Strings(JsonObject node, string key, bool lowercase = false) {
		if (node[key] is not JsonArray array || array.Count == 0) return NoStrings;

		List<string> results = [with(array.Count)];

		foreach (JsonNode? item in array) {
			if (item is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0) {
				results.Add(lowercase ? text.ToLowerInvariant() : text);
			}
		}

		return results.Count == 0 ? NoStrings : [.. results];
	}
}
