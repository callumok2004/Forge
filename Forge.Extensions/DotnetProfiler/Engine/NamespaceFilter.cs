using System.Buffers;

namespace Forge.Extensions.DotnetProfiler.Engine;

public sealed class NamespaceFilter
{
	private static readonly SearchValues<char> Separators = SearchValues.Create(";,\r\n");
	private static readonly SearchValues<char> Wildcards = SearchValues.Create("*?");

	private readonly string[] _prefixes;
	private readonly string[] _patterns;

	private NamespaceFilter(string text, string[] prefixes, string[] patterns) {
		Text = text;
		_prefixes = prefixes;
		_patterns = patterns;
	}

	public string Text { get; }

	public static NamespaceFilter? Parse(string? text) {
		if (string.IsNullOrWhiteSpace(text)) return null;

		List<string> prefixes = [];
		List<string> patterns = [];
		ReadOnlySpan<char> rest = text;

		while (!rest.IsEmpty) {
			int separator = rest.IndexOfAny(Separators);
			ReadOnlySpan<char> pattern = (separator < 0 ? rest : rest[..separator]).Trim();
			rest = separator < 0 ? [] : rest[(separator + 1)..];

			while (!pattern.IsEmpty && pattern[^1] is '*' or '.') pattern = pattern[..^1].TrimEnd();

			if (pattern.IsEmpty) continue;

			(pattern.ContainsAny(Wildcards) ? patterns : prefixes).Add(pattern.ToString());
		}

		return prefixes.Count == 0 && patterns.Count == 0 ? null : new NamespaceFilter(text.Trim(), [.. prefixes], [.. patterns]);
	}

	private static bool IsBoundary(char c) => c is '.' or '+' or '(' or '<' or '[';

	public bool Matches(ReadOnlySpan<char> name) {
		if (name.IsEmpty) return false;

		foreach (string prefix in _prefixes) {
			if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

			if (name.Length == prefix.Length || IsBoundary(name[prefix.Length])) return true;
		}

		foreach (string pattern in _patterns) {
			if (Glob(pattern, name)) return true;
		}

		return false;
	}

	public static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name) {
		if (pattern.IsEmpty) return name.IsEmpty || IsBoundary(name[0]);

		char head = pattern[0];

		if (head == '*') {
			for (int i = 0; ; i++) {
				if (Glob(pattern[1..], name[i..])) return true;

				if (i >= name.Length || name[i] == '.') return false;
			}
		}

		if (name.IsEmpty) return false;

		if (head != '?' && char.ToLowerInvariant(head) != char.ToLowerInvariant(name[0])) return false;

		return Glob(pattern[1..], name[1..]);
	}
}
