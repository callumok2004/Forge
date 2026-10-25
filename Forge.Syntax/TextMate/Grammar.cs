using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Syntax.TextMate;

public sealed class GrammarRule
{
	public string? Name { get; init; }

	public string? ContentName { get; init; }

	public string? Match { get; init; }

	public string? Begin { get; init; }

	public string? End { get; init; }

	public string? While { get; init; }

	public string? Include { get; init; }

	public Dictionary<int, string>? Captures { get; init; }

	public Dictionary<int, string>? BeginCaptures { get; init; }

	public Dictionary<int, string>? EndCaptures { get; init; }

	public List<GrammarRule> Patterns { get; init; } = [];

	public Dictionary<int, GrammarRule>? CaptureRules { get; init; }

	public Dictionary<int, GrammarRule>? BeginCaptureRules { get; init; }

	public Dictionary<int, GrammarRule>? EndCaptureRules { get; init; }

	private TieredRegex? _match;
	private TieredRegex? _begin;
	private TieredRegex? _end;
	private TieredRegex? _while;
	private bool _compiled;

	public TieredRegex? MatchRegex => Compiled()._match;

	public TieredRegex? BeginRegex => Compiled()._begin;

	public TieredRegex? EndRegex => Compiled()._end;

	public TieredRegex? WhileRegex => Compiled()._while;

	private GrammarRule Compiled() {
		if (_compiled) return this;

		_match = Match is { } match ? TieredRegex.For(match) : null;
		_begin = Begin is { } begin ? TieredRegex.For(begin) : null;
		_end = End is { } end ? TieredRegex.For(end) : null;
		_while = While is { } loop ? TieredRegex.For(loop) : null;
		_compiled = true;

		return this;
	}
}

public sealed class Grammar
{
	private readonly Dictionary<string, GrammarRule> _repository;

	private Grammar(string scopeName, List<GrammarRule> patterns,
		Dictionary<string, GrammarRule> repository) {
		ScopeName = scopeName;
		Patterns = patterns;
		_repository = repository;
	}

	public string ScopeName { get; }

	public List<GrammarRule> Patterns { get; }

	public IReadOnlyList<GrammarRule> Resolve(GrammarRule rule) {
		if (rule.Include is not { Length: > 0 } include) return rule.Patterns;

		if (include is "$self" or "$base") return Patterns;

		if (include[0] == '#') {
			return _repository.TryGetValue(include[1..], out GrammarRule? found)
				? [found]
				: [];
		}

		return [];
	}

	public static Grammar? Load(string path) {
		try {
			using FileStream stream = File.OpenRead(path);
			using JsonDocument document = JsonDocument.Parse(stream);

			JsonElement root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Object) return null;

			Dictionary<string, GrammarRule> repository = [with(StringComparer.Ordinal)];

			if (root.TryGetProperty("repository", out JsonElement entries) &&
				entries.ValueKind == JsonValueKind.Object) {
				foreach (JsonProperty entry in entries.EnumerateObject()) {
					if (ReadRule(entry.Value) is { } rule) repository[entry.Name] = rule;
				}
			}

			return new Grammar(Text(root, "scopeName") ?? string.Empty, ReadPatterns(root, "patterns"),
				repository);
		}
		catch (Exception exception) when (exception is IOException or JsonException
			or UnauthorizedAccessException) {
			return null;
		}
	}

	private static string? Text(in JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static List<GrammarRule> ReadPatterns(in JsonElement element, string name) {
		List<GrammarRule> rules = [];

		if (!element.TryGetProperty(name, out JsonElement array) ||
			array.ValueKind != JsonValueKind.Array) return rules;

		foreach (JsonElement entry in array.EnumerateArray()) {
			if (ReadRule(entry) is { } rule) rules.Add(rule);
		}

		return rules;
	}

	private static GrammarRule? ReadRule(in JsonElement entry) {
		if (entry.ValueKind != JsonValueKind.Object) return null;

		(Dictionary<int, string>? captures, Dictionary<int, GrammarRule>? captureRules) =
			ReadCaptures(entry, "captures");

		(Dictionary<int, string>? beginCaptures, Dictionary<int, GrammarRule>? beginCaptureRules) =
			ReadCaptures(entry, "beginCaptures");

		(Dictionary<int, string>? endCaptures, Dictionary<int, GrammarRule>? endCaptureRules) =
			ReadCaptures(entry, "endCaptures");

		return new GrammarRule {
			Name = Text(entry, "name"),
			ContentName = Text(entry, "contentName"),
			Match = Text(entry, "match"),
			Begin = Text(entry, "begin"),
			End = Text(entry, "end"),
			While = Text(entry, "while"),
			Include = Text(entry, "include"),
			Captures = captures,
			CaptureRules = captureRules,
			BeginCaptures = beginCaptures,
			BeginCaptureRules = beginCaptureRules,
			EndCaptures = endCaptures,
			EndCaptureRules = endCaptureRules,
			Patterns = ReadPatterns(entry, "patterns"),
		};
	}

	private static (Dictionary<int, string>?, Dictionary<int, GrammarRule>?) ReadCaptures(
		in JsonElement owner, string name) {
		if (!owner.TryGetProperty(name, out JsonElement entries) ||
			entries.ValueKind != JsonValueKind.Object) return (null, null);

		Dictionary<int, string>? names = null;
		Dictionary<int, GrammarRule>? rules = null;

		foreach (JsonProperty entry in entries.EnumerateObject()) {
			if (!int.TryParse(entry.Name, out int group) ||
				entry.Value.ValueKind != JsonValueKind.Object) continue;

			if (Text(entry.Value, "name") is { Length: > 0 } scope) (names ??= [])[group] = scope;

			if (entry.Value.TryGetProperty("patterns", out JsonElement patterns) &&
				patterns.ValueKind == JsonValueKind.Array) (rules ??= [])[group] = ReadRule(entry.Value)!;
		}

		return (names, rules);
	}
}
