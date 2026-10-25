using System.Text;
using System.Text.Json.Nodes;

using Forge.Core;

namespace Forge.Extensions.Dotnet.Server;

public static class LanguageServerOptions
{
	public static JsonArray Resolve(JsonNode? parameters, Settings? settings) {
		JsonArray answer = [];

		foreach (JsonNode? item in parameters?["items"] as JsonArray ?? []) {
			bool scoped = item?["scopeUri"] is not null;
			answer.Add(scoped ? null : Value(item?["section"]?.GetValue<string>(), settings));
		}

		return answer;
	}

	private static JsonNode? Value(string? section, Settings? settings) {
		if (section is null || settings is null || ClientName(section) is not { } name) return null;

		return settings.GetRaw(name) ?? (name.StartsWith("csharp.", StringComparison.Ordinal) ? settings.GetRaw(string.Concat("dotnet.", name.AsSpan(7))) : null) ?? Equivalent(name, settings);
	}

	public static string? ClientName(string section) {
		int bar = section.IndexOf('|');

		if (bar >= 0 && !section.AsSpan(0, bar).SequenceEqual("csharp")) return null;

		int dot = section.LastIndexOf('.');

		if (dot < 0) return null;

		string option = section[(dot + 1)..];
		string group = section[(bar + 1)..dot];
		string prefix = option.StartsWith("dotnet", StringComparison.Ordinal) ? "dotnet" : option.StartsWith("csharp", StringComparison.Ordinal) ? "csharp" : string.Empty;
		string feature = prefix.Length == 0 ? option : option[(prefix.Length + 1)..];

		return prefix.Length == 0 ? $"{CamelCase(group)}.{CamelCase(feature)}" : $"{prefix}.{CamelCase(group)}.{CamelCase(feature)}";
	}

	private static string CamelCase(string text) {
		string[] words = text.Split('_');

		if (words.Length <= 1) return text.ToLowerInvariant();

		StringBuilder builder = new(words[0].ToLowerInvariant());

		for (int i = 1; i < words.Length; i++) {
			if (words[i].Length == 0) continue;

			builder.Append(char.ToUpperInvariant(words[i][0])).Append(words[i].AsSpan(1).ToString().ToLowerInvariant());
		}

		return builder.ToString();
	}

	private static JsonNode? Equivalent(string name, Settings settings) => name switch {
		"codeStyle.formatting.indentationAndSpacing.tabWidth" => (settings.GetInt32("editor.tabSize") ?? 4).ToString(),
		"codeStyle.formatting.indentationAndSpacing.indentSize" => settings.GetInt32("editor.indentSize") is { } size ? size.ToString() : (settings.GetInt32("editor.tabSize") ?? 4).ToString(),
		"codeStyle.formatting.indentationAndSpacing.indentStyle" => settings.GetBoolean("editor.insertSpaces") ?? true ? "space" : "tab",
		"codeStyle.formatting.newLine.endOfLine" => (settings.GetString("files.eol") ?? "auto") switch {
			"\n" => "lf",
			"\r\n" => "crlf",
			string other => other,
		},
		"codeStyle.formatting.newLine.insertFinalNewline" => (settings.GetBoolean("files.insertFinalNewline") ?? false).ToString().ToLowerInvariant(),
		_ => null,
	};
}
