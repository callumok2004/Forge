using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Search;

public sealed class GlobSet
{
	private readonly Regex? _regex;

	private GlobSet(Regex? regex) => _regex = regex;

	public static readonly GlobSet Empty = new(null);

	public bool IsEmpty => _regex is null;

	public bool Matches(ReadOnlySpan<char> relativePath) => _regex is { } regex && regex.IsMatch(relativePath);

	public static GlobSet Parse(ReadOnlySpan<char> patterns) => Create(Split(patterns));

	public static GlobSet Create(IEnumerable<string> patterns) {
		StringBuilder builder = new("^(?:");
		bool any = false;

		foreach (string raw in patterns) {
			string pattern = raw.Trim().Replace('\\', '/');

			if (pattern.Length == 0) continue;

			if (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern[2..];
			else if (!pattern.StartsWith("**/", StringComparison.Ordinal) && !pattern.StartsWith('/')) pattern = "**/" + pattern;

			pattern = pattern.TrimStart('/');

			if (any) builder.Append('|');

			any = true;
			builder.Append("(?:");
			Translate(pattern.TrimEnd('/'), builder);
			builder.Append(")(?:/.*)?");
		}

		if (!any) return Empty;

		builder.Append(")$");
		return new GlobSet(new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
	}

	private static List<string> Split(ReadOnlySpan<char> patterns) {
		List<string> result = [];
		int depth = 0;
		int start = 0;

		for (int i = 0; i < patterns.Length; i++) {
			char character = patterns[i];

			if (character == '{') depth++;
			else if (character == '}') depth = Math.Max(0, depth - 1);
			else if (character == ',' && depth == 0) {
				result.Add(patterns[start..i].ToString());
				start = i + 1;
			}
		}

		result.Add(patterns[start..].ToString());
		return result;
	}

	private static void Translate(string glob, StringBuilder builder) {
		for (int i = 0; i < glob.Length; i++) {
			char character = glob[i];

			switch (character) {
				case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
					i++;

					if (i + 1 < glob.Length && glob[i + 1] == '/') {
						i++;
						builder.Append("(?:.*/)?");
					}
					else {
						builder.Append(".*");
					}

					break;

				case '*':
					builder.Append("[^/]*");
					break;

				case '?':
					builder.Append("[^/]");
					break;

				case '{':
					builder.Append("(?:");
					break;

				case '}':
					builder.Append(')');
					break;

				case ',':
					builder.Append('|');
					break;

				case '[':
					int close = glob.IndexOf(']', i + 1);

					if (close < 0) {
						builder.Append("\\[");
						break;
					}

					builder.Append('[').Append(glob.AsSpan(i + 1, close - i - 1).ToString().Replace("!", "^")).Append(']');
					i = close;
					break;

				default:
					builder.Append(Regex.Escape(character.ToString()));
					break;
			}
		}
	}
}
