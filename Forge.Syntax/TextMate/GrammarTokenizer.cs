using System.Text.RegularExpressions;

namespace Forge.Syntax.TextMate;

public readonly record struct ScopedToken(int Start, int Length, IReadOnlyList<string> Scopes);

public sealed class GrammarState
{
	public static readonly GrammarState Root = new(null, null, [], null, null, -1);

	private GrammarState(GrammarState? parent, GrammarRule? rule, IReadOnlyList<string> scopes,
		string? endPattern, string? whilePattern, int enterPosition) {
		Parent = parent;
		Rule = rule;
		Scopes = scopes;
		EndPattern = endPattern;
		WhilePattern = whilePattern;
		EnterPosition = enterPosition;
	}

	public int EnterPosition { get; }

	public GrammarState? Parent { get; }

	public GrammarRule? Rule { get; }

	public IReadOnlyList<string> Scopes { get; }

	public string? EndPattern { get; }

	public string? WhilePattern { get; }

	public GrammarState Push(GrammarRule rule, IReadOnlyList<string> scopes, string? endPattern,
		string? whilePattern, int enterPosition = -1) =>
		new(this, rule, scopes, endPattern, whilePattern, enterPosition);

	public GrammarState Pop() => Parent ?? Root;

	public int Depth => Parent is null ? 0 : Parent.Depth + 1;
}

public sealed partial class GrammarTokenizer(Grammar grammar)
{
	private const int MaxStepsPerLine = 10_000;

	public Grammar Grammar { get; } = grammar;

	public Action<string>? Trace { get; set; }

	public GrammarState Tokenize(string line, GrammarState state, List<ScopedToken> into) =>
		Tokenize(line, state, into, 0, null);

	private readonly record struct CachedMatch(int From, Match? Result);

	private readonly Stack<Dictionary<TieredRegex, CachedMatch>> _cachePool = [];
	private readonly Stack<HashSet<(GrammarRule, int)>> _exhaustedPool = [];

	private static Match? Search(TieredRegex regex, string line, int position, Dictionary<TieredRegex, CachedMatch> cache) {
		if (cache.TryGetValue(regex, out CachedMatch cached) && cached.From <= position && (cached.Result is null || cached.Result.Index >= position)) return cached.Result;

		Match match = regex.Use().Match(line, position);
		Match? result = match.Success ? match : null;

		if (!regex.AnchorsAtStart) cache[regex] = new CachedMatch(position, result);

		return result;
	}

	public GrammarState Tokenize(string line, GrammarState state, List<ScopedToken> into, int from,
		IReadOnlyList<string>? baseScopes) {
		state = ContinueWhile(line, state);

		IReadOnlyList<string>?[] map = System.Buffers.ArrayPool<IReadOnlyList<string>?>.Shared.Rent(Math.Max(line.Length, 1));
		Dictionary<TieredRegex, CachedMatch> cache = _cachePool.Count > 0 ? _cachePool.Pop() : new(ReferenceEqualityComparer.Instance);
		HashSet<(GrammarRule, int)> exhausted = _exhaustedPool.Count > 0 ? _exhaustedPool.Pop() : [];

		try {
			return Tokenize(line, state, into, from, baseScopes, map.AsSpan(0, line.Length), cache, exhausted);
		}
		finally {
			Array.Clear(map, 0, line.Length);
			System.Buffers.ArrayPool<IReadOnlyList<string>?>.Shared.Return(map);
			cache.Clear();
			_cachePool.Push(cache);
			exhausted.Clear();
			_exhaustedPool.Push(exhausted);
		}
	}

	private GrammarState Tokenize(string line, GrammarState state, List<ScopedToken> into, int from,
		IReadOnlyList<string>? baseScopes, Span<IReadOnlyList<string>?> map, Dictionary<TieredRegex, CachedMatch> cache, HashSet<(GrammarRule, int)> exhausted) {
		if (baseScopes is not null) state = state.Push(NoRule, baseScopes, null, null);

		IReadOnlyList<string> outermost = state.Scopes;

		int position = from;
		int steps = 0;

		while (position <= line.Length && steps++ < MaxStepsPerLine) {
			Match? endMatch = state.EndPattern is { } pattern && TieredRegex.For(pattern) is { } endRegex ? Search(endRegex, line, position, cache) : null;

			(GrammarRule? rule, Match? match) = Best(line, position, state, exhausted, cache);

			bool ending = endMatch is not null && (match is null || endMatch.Index <= match.Index);

			if (ending) {
				Trace?.Invoke($"end @{endMatch!.Index}+{endMatch.Length} [{endMatch.Value}]");

				Paint(map, position, endMatch!.Index - position, state.Scopes);

				IReadOnlyList<string> closingScopes = state.Rule is null
					? state.Scopes
					: state.Parent?.Scopes is { } outer
						? With(outer, state.Rule.Name, endMatch)
						: state.Scopes;

				Paint(map, endMatch!.Index, endMatch.Length, closingScopes);

				if (state.Rule is { } closing) {
					PaintCaptures(map, line, closingScopes, endMatch!, closing.EndCaptures,
						closing.EndCaptureRules);
				}

				int after = endMatch!.Index + endMatch.Length;
				bool advanced = after > position;

				GrammarState popped = state;
				state = state.Pop();

				if (advanced) {
					position = after;
					continue;
				}

				if (popped.EnterPosition == position) {
					Trace?.Invoke($"loop guard at {position}");
					state = popped;
					break;
				}

				continue;
			}

			if (rule is null || match is null) {
				Trace?.Invoke($"stop at {position} (no rule, no end)");
				break;
			}

			Paint(map, position, match.Index - position, state.Scopes);

			if (Trace is { } trace) {
				string source = rule.Match ?? rule.Begin ?? string.Empty;

				trace($"{(rule.MatchRegex is not null ? "match" : "begin")} @{match.Index}+" +
					$"{match.Length} [{match.Value}] name={rule.Name} " +
					$"pat={source[..Math.Min(40, source.Length)].Replace('\n', ' ')}");
			}

			if (rule.MatchRegex is not null) {
				IReadOnlyList<string> scopes = With(state.Scopes, rule.Name, match);

				Paint(map, match.Index, match.Length, scopes);
				PaintCaptures(map, line, scopes, match, rule.Captures, rule.CaptureRules);

				position = match.Length > 0 ? match.Index + match.Length : match.Index + 1;
				continue;
			}

			IReadOnlyList<string> opened = With(state.Scopes, rule.Name, match);

			Paint(map, match.Index, match.Length, opened);
			PaintCaptures(map, line, opened, match, rule.BeginCaptures, rule.BeginCaptureRules);

			if (match.Length == 0) exhausted.Add((rule, match.Index));

			state = state.Push(rule, With(opened, rule.ContentName, match),
				Substitute(rule.End, match), Substitute(rule.While, match), position);

			position = match.Index + match.Length;
		}

		if (position < line.Length) Paint(map, position, line.Length - position, state.Scopes);

		Coalesce(map, outermost, into);
		return state;
	}

	private static readonly GrammarRule NoRule = new();

	private static GrammarState ContinueWhile(string line, GrammarState state) => FailedWhile(line, state) ?? state;

	private static GrammarState? FailedWhile(string line, GrammarState? state) {
		if (state is null || state.Rule is null) return null;

		if (FailedWhile(line, state.Parent) is { } outer) return outer;

		if (state.WhilePattern is not { } pattern) return null;

		return TieredRegex.For(pattern) is not { } regex || !regex.Use().IsMatch(line) ? state.Pop() : null;
	}

	private (GrammarRule?, Match?) Best(string line, int position, GrammarState state,
		HashSet<(GrammarRule, int)> exhausted, Dictionary<TieredRegex, CachedMatch> cache) {
		GrammarRule? bestRule = null;
		Match? best = null;

		List<GrammarRule> candidates = Flatten(state.Rule?.Patterns ?? Grammar.Patterns);

		foreach (GrammarRule candidate in candidates) {
			TieredRegex? regex = candidate.MatchRegex ?? candidate.BeginRegex;

			if (regex is null) continue;

			if (Search(regex, line, position, cache) is not { } match || (best is not null && match.Index >= best.Index)) continue;

			if (match.Length == 0 && candidate.Begin is not null &&
				exhausted.Contains((candidate, match.Index))) continue;

			bestRule = candidate;
			best = match;

			if (match.Index == position) break;
		}

		return (bestRule, best);
	}

	private List<GrammarRule> Flatten(IReadOnlyList<GrammarRule> rules) {
		if (_flattened.TryGetValue(rules, out List<GrammarRule>? cached)) return cached;

		List<GrammarRule> flat = [];
		Expand(rules, flat, [with(ReferenceEqualityComparer.Instance)]);
		_flattened[rules] = flat;
		return flat;
	}

	private readonly Dictionary<IReadOnlyList<GrammarRule>, List<GrammarRule>> _flattened =
		[with(ReferenceEqualityComparer.Instance)];

	private void Expand(IReadOnlyList<GrammarRule> rules, List<GrammarRule> into,
		HashSet<GrammarRule> visited) {
		foreach (GrammarRule rule in rules) {
			if (!visited.Add(rule)) continue;

			if (rule.Include is { Length: > 0 }) {
				Expand(Grammar.Resolve(rule), into, visited);
				continue;
			}

			if (rule.Match is null && rule.Begin is null && rule.Patterns.Count > 0) {
				Expand(rule.Patterns, into, visited);
				continue;
			}

			into.Add(rule);
		}
	}

	private static IReadOnlyList<string> With(IReadOnlyList<string> scopes, string? name,
		Match? match = null) {
		if (name is not { Length: > 0 }) return scopes;

		if (match is not null && name.Contains('$')) {
			name = Substitution.Replace(name, found => {
				int group = int.Parse(found.Groups[1].Value);

				return group < match.Groups.Count && match.Groups[group].Success
					? match.Groups[group].Value
					: string.Empty;
			});
		}

		string[] extended = new string[scopes.Count + 1];

		for (int i = 0; i < scopes.Count; i++) extended[i] = scopes[i];

		extended[^1] = name;
		return extended;
	}

	private static readonly Regex Substitution = MyRegex();

	private static void Paint(Span<IReadOnlyList<string>?> map, int start, int length,
		IReadOnlyList<string> scopes) {
		int end = Math.Min(start + length, map.Length);

		for (int i = Math.Max(0, start); i < end; i++) map[i] = scopes;
	}

	private static void Coalesce(Span<IReadOnlyList<string>?> map, IReadOnlyList<string> fallback,
		List<ScopedToken> into) {
		int start = 0;

		for (int i = 1; i <= map.Length; i++) {
			if (i < map.Length && ReferenceEquals(map[i], map[start])) continue;

			into.Add(new ScopedToken(start, i - start, map[start] ?? fallback));
			start = i;
		}
	}

	private void PaintCaptures(Span<IReadOnlyList<string>?> map, string line, IReadOnlyList<string> scopes,
		Match match, Dictionary<int, string>? captures, Dictionary<int, GrammarRule>? captureRules) {
		if (captures is null && captureRules is null) return;

		for (int group = 0; group < match.Groups.Count; group++) {
			Group captured = match.Groups[group];

			if (!captured.Success || captured.Length == 0) continue;

			string? name = captures?.GetValueOrDefault(group);
			GrammarRule? nested = captureRules?.GetValueOrDefault(group);

			if (name is null && nested is null) continue;

			IReadOnlyList<string> captureScopes = With(scopes, name, match);
			Paint(map, captured.Index, captured.Length, captureScopes);

			if (nested is not null) Retokenize(map, line, captured, captureScopes, nested);
		}
	}

	private void Retokenize(Span<IReadOnlyList<string>?> map, string line, Group captured,
		IReadOnlyList<string> scopes, GrammarRule rule) {
		List<ScopedToken> inner = _innerPool.Count > 0 ? _innerPool.Pop() : [];

		GrammarState state = GrammarState.Root.Push(rule, scopes, null, null);

		Tokenize(line[..(captured.Index + captured.Length)], state, inner, captured.Index, null);

		foreach (ScopedToken token in inner) {
			if (token.Start >= captured.Index && token.Scopes.Count > scopes.Count) {
				Paint(map, token.Start, token.Length, token.Scopes);
			}
		}

		inner.Clear();
		_innerPool.Push(inner);
	}

	private readonly Stack<List<ScopedToken>> _innerPool = [];

	private static string? Substitute(string? pattern, Match begin) {
		if (pattern is null || !pattern.Contains('\\')) return pattern;

		System.Text.StringBuilder builder = new(pattern.Length);

		for (int i = 0; i < pattern.Length; i++) {
			if (pattern[i] == '\\' && i + 1 < pattern.Length && char.IsDigit(pattern[i + 1])) {
				int group = pattern[i + 1] - '0';

				builder.Append(group < begin.Groups.Count && begin.Groups[group].Success
					? Regex.Escape(begin.Groups[group].Value)
					: string.Empty);

				i++;
				continue;
			}

			builder.Append(pattern[i]);
		}

		return builder.ToString();
	}

	[GeneratedRegex(@"\$\{?(\d+)\}?", RegexOptions.CultureInvariant)]
	private static partial Regex MyRegex();
}
