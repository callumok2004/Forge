using System.Text.Json;

using Forge.Common.Extensions;
using Forge.Syntax;

namespace Forge.Extensions.Host;

public static class BuiltInContributions
{
	public static string Directory => Path.Combine(AppContext.BaseDirectory, "Extensions");

	public static void RegisterGrammars() {
		if (!System.IO.Directory.Exists(Directory)) return;

		foreach (string folder in System.IO.Directory.EnumerateDirectories(Directory)) {
			if (ExtensionManifest.Load(folder) is not { } manifest) continue;

			foreach (GrammarContribution grammar in manifest.Grammars) {
				if (grammar.Language is not { } id || manifest.Languages.FirstOrDefault(language => language.Id == id) is not { } language) continue;

				(string? line, string? blockStart, string? blockEnd) = Comments(language.ConfigurationPath);
				GrammarIndex.Register(new GrammarEntry(id, language.Aliases.FirstOrDefault() ?? id, grammar.ScopeName, grammar.Path, language.Extensions, language.FileNames, line, blockStart, blockEnd));
			}
		}
	}

	private static (string?, string?, string?) Comments(string? path) {
		if (path is null || !File.Exists(path)) return (null, null, null);

		try {
			using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

			if (!document.RootElement.TryGetProperty("comments", out JsonElement comments)) return (null, null, null);

			string? line = comments.TryGetProperty("lineComment", out JsonElement lineComment) ? lineComment.GetString() : null;

			if (comments.TryGetProperty("blockComment", out JsonElement block) && block.ValueKind == JsonValueKind.Array && block.GetArrayLength() == 2) return (line, block[0].GetString(), block[1].GetString());

			return (line, null, null);
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
			return (null, null, null);
		}
	}
}
