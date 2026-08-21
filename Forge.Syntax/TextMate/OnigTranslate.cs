using System.Text.RegularExpressions;
using System.Text;

namespace Forge.Syntax.TextMate;

public static class OnigTranslate
{
	private static readonly Dictionary<string, Regex> Cache = [with(StringComparer.Ordinal)];
	private static readonly Lock Sync = new();

	public static Regex? Compile(string pattern) {
		lock (Sync) {
			if (Cache.TryGetValue(pattern, out Regex? cached)) return cached;
		}

		Regex? compiled = null;

		try {
			compiled = new Regex(Translate(pattern), RegexOptions.Multiline | RegexOptions.CultureInvariant);
		}
		catch (ArgumentException) {
		}

		lock (Sync) Cache[pattern] = compiled!;

		return compiled;
	}

	public static readonly List<string> Unresolved = [];

	public static string Debug(string pattern) => Translate(pattern);

	private static string Translate(string pattern) {
		StringBuilder builder = new(pattern.Length + 16);

		bool extended = false;
		bool inClass = false;

		for (int i = 0; i < pattern.Length; i++) {
			char c = pattern[i];

			if (c == '\\' && i + 1 < pattern.Length) {
				char next = pattern[i + 1];

				switch (next) {
					case 'h':
						builder.Append("[0-9a-fA-F]");
						i++;
						continue;

					case 'H':
						builder.Append("[^0-9a-fA-F]");
						i++;
						continue;

					default:
						builder.Append(c).Append(next);
						i++;
						continue;
				}
			}

			if (c == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':' &&
				pattern.IndexOf(":]", i, StringComparison.Ordinal) is int close && close > 0) {
				string posix = pattern[(i + 2)..close];

				if (PosixClass(posix) is { } replacement) {
					builder.Append(inClass ? replacement : $"[{replacement}]");
					i = close + 1;
					continue;
				}
			}

			if (c == '(' && i + 2 < pattern.Length && pattern[i + 1] == '?' && pattern[i + 2] == '~') {
				int absentEnd = Balanced(pattern, i);

				if (absentEnd > 0) {
					builder.Append("(?:(?!").Append(pattern[(i + 3)..absentEnd]).Append(").)*");
					i = absentEnd;
					continue;
				}
			}

			switch (c) {
				case '[' when !inClass:
					inClass = true;
					builder.Append(c);
					continue;

				case ']' when inClass:
					inClass = false;
					builder.Append(c);
					continue;

				case '+' when !inClass && builder.Length > 0 && IsQuantifier(builder[^1]):
					Atomise(builder);
					continue;

				case '(' when !inClass && i + 3 < pattern.Length && pattern[i + 1] == '?' &&
					pattern[i + 2] == 'x' && pattern[i + 3] == ')':
					extended = true;
					i += 3;
					continue;

				default:
					builder.Append(c);
					continue;
			}
		}

		string translated = extended ? StripExtended(builder.ToString()) : builder.ToString();

		return StripGroupNames(ExpandSubroutines(translated));
	}

	private static string StripGroupNames(string pattern) {
		if (!pattern.Contains("(?<", StringComparison.Ordinal) &&
			!pattern.Contains("(?'", StringComparison.Ordinal)) return pattern;

		StringBuilder builder = new(pattern.Length);

		for (int i = 0; i < pattern.Length; i++) {
			if (pattern[i] == '\\' && i + 1 < pattern.Length) {
				builder.Append(pattern[i]).Append(pattern[i + 1]);
				i++;
				continue;
			}

			if (pattern[i] != '(' || i + 2 >= pattern.Length || pattern[i + 1] != '?' ||
				(pattern[i + 2] != '<' && pattern[i + 2] != '\'')) {
				builder.Append(pattern[i]);
				continue;
			}

			if (pattern[i + 2] == '<' && i + 3 < pattern.Length &&
				(pattern[i + 3] == '=' || pattern[i + 3] == '!')) {
				builder.Append(pattern[i]);
				continue;
			}

			int end = pattern.IndexOf(pattern[i + 2] == '<' ? '>' : '\'', i + 3);

			if (end < 0) {
				builder.Append(pattern[i]);
				continue;
			}

			builder.Append('(');
			i = end;
		}

		return builder.ToString();
	}

	private static string ExpandSubroutines(string pattern) {
		if (!pattern.Contains(@"\g<", StringComparison.Ordinal)) return pattern;

		Dictionary<string, string?> definitions = [with(StringComparer.Ordinal)];
		return Expand(pattern, pattern, definitions, []);
	}

	private const int RecursionLimit = 3;

	private static string Expand(string text, string original,
		Dictionary<string, string?> definitions, Dictionary<string, int> active) {
		int call = text.IndexOf(@"\g<", StringComparison.Ordinal);

		if (call < 0) return text;

		StringBuilder builder = new(text.Length);
		int cursor = 0;

		while (call >= 0) {
			builder.Append(text, cursor, call - cursor);

			int close = text.IndexOf('>', call);

			if (close < 0) {
				cursor = call;
				break;
			}

			string name = text[(call + 3)..close];

			if (!definitions.TryGetValue(name, out string? body)) {
				body = GroupBody(original, name);
				definitions[name] = body;
			}

			int depth = active.GetValueOrDefault(name);

			if (body is null || depth >= RecursionLimit) builder.Append("(?!)");
			else {
				active[name] = depth + 1;
				builder.Append("(?:").Append(Anonymise(Expand(body, original, definitions, active)))
					.Append(')');

				active[name] = depth;
			}

			cursor = close + 1;
			call = text.IndexOf(@"\g<", cursor, StringComparison.Ordinal);
		}

		builder.Append(text, cursor, text.Length - cursor);
		return builder.ToString();
	}

	private static string? GroupBody(string pattern, string name) {
		string opening = $"(?<{name}>";
		int start = pattern.IndexOf(opening, StringComparison.Ordinal);

		if (start < 0) return null;

		int at = start + opening.Length;
		int depth = 1;

		while (at < pattern.Length) {
			char c = pattern[at];

			if (c == '\\') {
				at += 2;
				continue;
			}

			if (c == '(') depth++;
			else if (c == ')' && --depth == 0) return pattern[(start + opening.Length)..at];

			at++;
		}

		return null;
	}

	private static string Anonymise(string fragment) {
		StringBuilder builder = new(fragment.Length);

		for (int i = 0; i < fragment.Length; i++) {
			if (fragment[i] == '\\' && i + 1 < fragment.Length) {
				builder.Append(fragment[i]).Append(fragment[i + 1]);
				i++;
				continue;
			}

			if (fragment[i] != '(') {
				builder.Append(fragment[i]);
				continue;
			}

			if (i + 2 < fragment.Length && fragment[i + 1] == '?' &&
				(fragment[i + 2] == '<' || fragment[i + 2] == '\'')) {
				if (fragment[i + 2] == '<' && i + 3 < fragment.Length &&
					(fragment[i + 3] == '=' || fragment[i + 3] == '!')) {
					builder.Append(fragment[i]);
					continue;
				}

				char terminator = fragment[i + 2] == '<' ? '>' : '\'';
				int end = fragment.IndexOf(terminator, i + 3);

				if (end > 0) {
					builder.Append("(?:");
					i = end;
					continue;
				}
			}

			if (i + 1 < fragment.Length && fragment[i + 1] == '?') {
				builder.Append(fragment[i]);
				continue;
			}

			builder.Append("(?:");
		}

		return builder.ToString();
	}

	private static string? PosixClass(string name) => name switch {
		"alpha" => @"\p{L}",
		"alnum" => @"\p{L}\p{Nd}",
		"digit" => @"\p{Nd}",
		"upper" => @"\p{Lu}",
		"lower" => @"\p{Ll}",
		"space" => @"\s",
		"blank" => @" \t",
		"punct" => @"\p{P}\p{S}",
		"word" => @"\w",
		"xdigit" => "0-9a-fA-F",
		"cntrl" => @"\p{Cc}",
		"print" => @"\P{Cc}",
		"graph" => @"\P{Cc}\S",
		_ => null,
	};

	private static int Balanced(string pattern, int open) {
		int depth = 0;

		for (int i = open; i < pattern.Length; i++) {
			if (pattern[i] == '\\') {
				i++;
				continue;
			}

			if (pattern[i] == '(') depth++;
			else if (pattern[i] == ')' && --depth == 0) return i;
		}

		return -1;
	}

	private static bool IsQuantifier(char c) => c is '*' or '+' or '?' or '}';

	private static void Atomise(StringBuilder builder) {
		int quantifier = builder.Length - 1;
		int start = quantifier;

		if (builder[start] == '}') while (start > 0 && builder[start] != '{') start--;

		start--;

		if (start >= 0 && builder[start] == ')') {
			int depth = 0;

			while (start >= 0) {
				if (builder[start] == ')') depth++;
				else if (builder[start] == '(') {
					depth--;

					if (depth == 0) break;
				}

				start--;
			}
		}
		else if (start >= 0 && builder[start] == ']') while (start > 0 && builder[start] != '[') start--;
		else if (start > 0 && builder[start - 1] == '\\') start--;

		if (start < 0) {
			builder.Append('+');
			return;
		}

		string atom = builder.ToString(start, builder.Length - start);
		builder.Length = start;
		builder.Append("(?>").Append(atom).Append(')');
	}

	private static string StripExtended(string pattern) {
		StringBuilder builder = new(pattern.Length);
		bool inClass = false;

		for (int i = 0; i < pattern.Length; i++) {
			char c = pattern[i];

			if (c == '\\' && i + 1 < pattern.Length) {
				builder.Append(c).Append(pattern[i + 1]);
				i++;
				continue;
			}

			if (c == '[') inClass = true;
			else if (c == ']') inClass = false;

			if (!inClass) {
				if (char.IsWhiteSpace(c)) continue;

				if (c == '#') {
					while (i < pattern.Length && pattern[i] != '\n') i++;

					continue;
				}
			}

			builder.Append(c);
		}

		return builder.ToString();
	}
}
