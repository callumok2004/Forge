using System.Text;
using System.Text.RegularExpressions;

using Forge.Text;

namespace Forge.Editor;

public readonly record struct FindOptions(bool MatchCase, bool WholeWord, bool Regex);

public readonly record struct FindMatch(int Start, int Length);

public sealed class FindModel
{
	public const int MatchesLimit = 19999;

	private const string WordSeparators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";

	private readonly List<FindMatch> _matches = [];
	private int _version = -1;
	private string _query = string.Empty;
	private FindOptions _options;
	private (int Start, int End)? _scope;
	private Regex? _regex;

	public IReadOnlyList<FindMatch> Matches => _matches;

	public bool InvalidPattern { get; private set; }

	public bool Update(TextSnapshot snapshot, string query, FindOptions options, (int Start, int End)? scope) {
		if (snapshot.Version == _version && query == _query && options == _options && scope == _scope) return false;

		_version = snapshot.Version;
		_query = query;
		_options = options;
		_scope = scope;
		_matches.Clear();
		InvalidPattern = false;
		_regex = null;

		if (query.Length == 0) return true;

		string text = snapshot.GetText();
		int from = scope?.Start ?? 0;
		int to = Math.Min(scope?.End ?? text.Length, text.Length);

		if (options.Regex) {
			try {
				_regex = new Regex(query, RegexOptions.Multiline | RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(2));
			}
			catch (ArgumentException) {
				InvalidPattern = true;
				return true;
			}

			try {
				Match match = _regex.Match(text, from, to - from);

				while (match.Success && _matches.Count < MatchesLimit) {
					if (match.Length > 0 && (!options.WholeWord || IsWholeWord(text, match.Index, match.Length))) _matches.Add(new FindMatch(match.Index, match.Length));

					match = match.NextMatch();
				}
			}
			catch (RegexMatchTimeoutException) {
			}

			return true;
		}

		StringComparison comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		ReadOnlySpan<char> span = text.AsSpan(0, to);
		int at = from;

		while (at <= to - query.Length && _matches.Count < MatchesLimit) {
			int found = span[at..].IndexOf(query, comparison);

			if (found < 0) break;

			int start = at + found;

			if (!options.WholeWord || IsWholeWord(text, start, query.Length)) _matches.Add(new FindMatch(start, query.Length));

			at = start + Math.Max(1, query.Length);
		}

		return true;
	}

	private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || WordSeparators.Contains(c);

	private static bool IsWholeWord(string text, int start, int length) {
		bool leftOk = start == 0 || IsSeparator(text[start - 1]) || IsSeparator(text[start]);
		int end = start + length;
		bool rightOk = end >= text.Length || IsSeparator(text[end]) || IsSeparator(text[end - 1]);
		return leftOk && rightOk;
	}

	public int IndexAtOrAfter(int offset) {
		int low = 0;
		int high = _matches.Count;

		while (low < high) {
			int mid = (low + high) >>> 1;

			if (_matches[mid].Start < offset) low = mid + 1;
			else high = mid;
		}

		return low;
	}

	public int IndexOf(int start, int length) {
		int index = IndexAtOrAfter(start);
		return index < _matches.Count && _matches[index].Start == start && _matches[index].Length == length ? index : -1;
	}

	public int Next(int offset) {
		if (_matches.Count == 0) return -1;

		int index = IndexAtOrAfter(offset);
		return index >= _matches.Count ? 0 : index;
	}

	public int Previous(int offset) {
		if (_matches.Count == 0) return -1;

		int index = IndexAtOrAfter(offset) - 1;
		return index < 0 ? _matches.Count - 1 : index;
	}

	public string ReplacementFor(string text, FindMatch target, string replacement, bool preserveCase) {
		string result = replacement;
		string matched = text.Substring(target.Start, target.Length);

		if (_options.Regex && _regex is not null) {
			Match match = _regex.Match(text, target.Start);
			result = match.Success && match.Index == target.Start ? match.Result(Unescape(replacement)) : Unescape(replacement);
		}

		return preserveCase ? PreserveCase(matched, result) : result;
	}

	private static string Unescape(string replacement) {
		StringBuilder builder = new(replacement.Length);

		for (int i = 0; i < replacement.Length; i++) {
			char c = replacement[i];

			if (c == '\\' && i + 1 < replacement.Length) {
				char next = replacement[i + 1];

				switch (next) {
					case 'n':
						builder.Append('\n');
						i++;
						continue;
					case 't':
						builder.Append('\t');
						i++;
						continue;
					case '\\':
						builder.Append('\\');
						i++;
						continue;
				}
			}

			builder.Append(c);
		}

		return builder.ToString();
	}

	public static string PreserveCase(string matched, string replacement) {
		if (matched.Length == 0 || replacement.Length == 0) return replacement;

		bool hasLetter = false;
		bool allUpper = true;
		bool allLower = true;

		foreach (char c in matched) {
			if (!char.IsLetter(c)) continue;

			hasLetter = true;
			allUpper &= char.IsUpper(c);
			allLower &= char.IsLower(c);
		}

		if (!hasLetter) return replacement;

		if (allUpper && matched.Length > 1) return replacement.ToUpperInvariant();

		if (allLower) return replacement.ToLowerInvariant();

		if (char.IsUpper(matched[0])) return char.ToUpperInvariant(replacement[0]) + replacement[1..];

		return replacement;
	}
}
