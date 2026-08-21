using System.Text.Json.Nodes;
using System.Text.Json;

using Forge.Theming;

namespace Forge.Syntax.TextMate;

public sealed class ScopeTheme
{
	private readonly List<Rule> _rules = [];

	private ScopeTheme(Color foreground) {
		Foreground = foreground;
	}

	public Color Foreground { get; }

	private readonly record struct Rule(string[] Path, Color Color, int Order)
	{
		public int Specificity {
			get {
				int total = 0;

				foreach (string part in Path) total += part.Count(static c => c == '.') + 1;

				return total;
			}
		}
	}

	public Color ColorFor(IReadOnlyList<string> scopes) {
		for (int at = scopes.Count - 1; at >= 0; at--) {
			Color best = Foreground;
			int bestSpecificity = -1;
			int bestOrder = -1;

			foreach (Rule rule in _rules) {
				if (!IsPrefix(rule.Path[^1], scopes[at]) || !Matches(rule.Path, scopes, at)) continue;

				int specificity = rule.Specificity;

				if (specificity < bestSpecificity ||
					(specificity == bestSpecificity && rule.Order < bestOrder)) continue;

				best = rule.Color;
				bestSpecificity = specificity;
				bestOrder = rule.Order;
			}

			if (bestSpecificity >= 0) return best;
		}

		return Foreground;
	}

	private static bool Matches(string[] path, IReadOnlyList<string> scopes, int matchedAt) {
		int at = matchedAt - 1;

		for (int part = path.Length - 2; part >= 0; part--) {
			while (at >= 0 && !IsPrefix(path[part], scopes[at])) at--;

			if (at < 0) return false;

			at--;
		}

		return true;
	}

	private static bool IsPrefix(string selector, string scope) {
		if (!scope.StartsWith(selector, StringComparison.Ordinal)) return false;

		return scope.Length == selector.Length || scope[selector.Length] == '.';
	}

	public static ScopeTheme? Load(string path) {
		try {
			JsonDocumentOptions options = new() {
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			};

			if (JsonNode.Parse(File.ReadAllText(path), documentOptions: options) is not JsonObject root) return null;

			Color foreground = root["colors"]?["editor.foreground"]?.GetValue<string>() is { } editor &&
				Color.TryParse(editor, out Color parsed)
					? parsed
					: Theme.EditorForeground;

			ScopeTheme theme = new(foreground);
			int order = 0;

			foreach (JsonNode? entry in root["tokenColors"] as JsonArray ?? []) {
				if (entry?["settings"]?["foreground"]?.GetValue<string>() is not { } value ||
					!Color.TryParse(value, out Color colour)) {
					order++;
					continue;
				}

				foreach (string selector in Selectors(entry["scope"])) {
					string[] parts = selector.Split(' ',
						StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

					if (parts.Length > 0) theme._rules.Add(new Rule(parts, colour, order));
				}

				order++;
			}

			return theme;
		}
		catch (Exception exception) when (exception is IOException or JsonException
			or UnauthorizedAccessException) {
			return null;
		}
	}

	private static IEnumerable<string> Selectors(JsonNode? scope) {
		switch (scope) {
			case JsonArray array:
				foreach (JsonNode? entry in array) {
					if (entry?.GetValue<string>() is { Length: > 0 } value) {
						foreach (string part in value.Split(',')) yield return part.Trim();
					}
				}

				break;

			case JsonValue value when value.GetValue<string>() is { Length: > 0 } text:
				foreach (string part in text.Split(',')) yield return part.Trim();

				break;
		}
	}
}
