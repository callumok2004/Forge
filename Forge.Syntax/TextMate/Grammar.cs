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

	public Grammar? Owner { get; internal set; }

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

	private Grammar(string scopeName, List<GrammarRule> patterns, Dictionary<string, GrammarRule> repository, List<(ScopeSelector, GrammarRule)> injections) {
		ScopeName = scopeName;
		Patterns = patterns;
		_repository = repository;
		Injections = injections;
	}

	public IReadOnlyList<(ScopeSelector Selector, GrammarRule Rule)> Injections { get; }

	public string ScopeName { get; }

	public List<GrammarRule> Patterns { get; }

	public IReadOnlyList<GrammarRule> Resolve(GrammarRule rule) {
		if (rule.Include is not { Length: > 0 } include) return rule.Patterns;

		Grammar owner = rule.Owner ?? this;

		if (include == "$self") return owner.Patterns;

		if (include == "$base") return Patterns;

		if (include[0] == '#') return owner.Repository(include.AsSpan(1));

		int hash = include.IndexOf('#');
		string scope = hash < 0 ? include : include[..hash];
		Grammar? external = scope == owner.ScopeName ? owner : Grammars.ForScope(scope);

		if (external is null) return [];

		return hash < 0 ? external.Patterns : external.Repository(include.AsSpan(hash + 1));
	}

	private IReadOnlyList<GrammarRule> Repository(ReadOnlySpan<char> name) =>
		_repository.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out GrammarRule? found) ? [found] : [];

	public static Grammar? Load(string path) {
		try {
			using FileStream stream = File.OpenRead(path);
			using JsonDocument document = JsonDocument.Parse(stream);

			JsonElement root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Object) return null;

			Dictionary<string, GrammarRule> repository = [with(StringComparer.Ordinal)];
			List<GrammarRule> all = [];

			if (root.TryGetProperty("repository", out JsonElement entries) &&
				entries.ValueKind == JsonValueKind.Object) {
				foreach (JsonProperty entry in entries.EnumerateObject()) {
					if (ReadRule(entry.Value, all) is { } rule) repository[entry.Name] = rule;
				}
			}

			List<(ScopeSelector, GrammarRule)> injections = [];

			if (root.TryGetProperty("injections", out JsonElement injected) && injected.ValueKind == JsonValueKind.Object) {
				foreach (JsonProperty entry in injected.EnumerateObject()) {
					if (ReadRule(entry.Value, all) is { } rule) injections.Add((ScopeSelector.Parse(entry.Name), rule));
				}
			}

			Grammar grammar = new(Text(root, "scopeName") ?? string.Empty, ReadPatterns(root, "patterns", all), repository, injections);

			foreach (GrammarRule rule in all) rule.Owner = grammar;

			return grammar;
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

	private static List<GrammarRule> ReadPatterns(in JsonElement element, string name, List<GrammarRule> all) {
		List<GrammarRule> rules = [];

		if (!element.TryGetProperty(name, out JsonElement array) ||
			array.ValueKind != JsonValueKind.Array) return rules;

		foreach (JsonElement entry in array.EnumerateArray()) {
			if (ReadRule(entry, all) is { } rule) rules.Add(rule);
		}

		return rules;
	}

	private static GrammarRule? ReadRule(in JsonElement entry, List<GrammarRule> all) {
		if (entry.ValueKind != JsonValueKind.Object) return null;

		(Dictionary<int, string>? captures, Dictionary<int, GrammarRule>? captureRules) =
			ReadCaptures(entry, "captures", all);

		(Dictionary<int, string>? beginCaptures, Dictionary<int, GrammarRule>? beginCaptureRules) =
			ReadCaptures(entry, "beginCaptures", all);

		(Dictionary<int, string>? endCaptures, Dictionary<int, GrammarRule>? endCaptureRules) =
			ReadCaptures(entry, "endCaptures", all);

		GrammarRule rule = new() {
			Name = Text(entry, "name"),
			ContentName = Text(entry, "contentName"),
			Match = Text(entry, "match"),
			Begin = Text(entry, "begin"),
			End = Text(entry, "end"),
			While = Text(entry, "while"),
			Include = Text(entry, "include"),
			Captures = captures,
			CaptureRules = captureRules,
			BeginCaptures = beginCaptures ?? (beginCaptureRules is null ? captures : null),
			BeginCaptureRules = beginCaptureRules ?? (beginCaptures is null ? captureRules : null),
			EndCaptures = endCaptures ?? (endCaptureRules is null ? captures : null),
			EndCaptureRules = endCaptureRules ?? (endCaptures is null ? captureRules : null),
			Patterns = ReadPatterns(entry, "patterns", all),
		};

		all.Add(rule);
		return rule;
	}

	private static (Dictionary<int, string>?, Dictionary<int, GrammarRule>?) ReadCaptures(
		in JsonElement owner, string name, List<GrammarRule> all) {
		if (!owner.TryGetProperty(name, out JsonElement entries) ||
			entries.ValueKind != JsonValueKind.Object) return (null, null);

		Dictionary<int, string>? names = null;
		Dictionary<int, GrammarRule>? rules = null;

		foreach (JsonProperty entry in entries.EnumerateObject()) {
			if (!int.TryParse(entry.Name, out int group) ||
				entry.Value.ValueKind != JsonValueKind.Object) continue;

			if (Text(entry.Value, "name") is { Length: > 0 } scope) (names ??= [])[group] = scope;

			if (entry.Value.TryGetProperty("patterns", out JsonElement patterns) &&
				patterns.ValueKind == JsonValueKind.Array) (rules ??= [])[group] = ReadRule(entry.Value, all)!;
		}

		return (names, rules);
	}
}
