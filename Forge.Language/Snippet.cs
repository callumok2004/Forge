using System.Text;

namespace Forge.Language;

public static class Snippet
{
	public static (string Text, int Caret) Expand(string snippet) {
		StringBuilder text = new(snippet.Length);
		int caret = -1;
		int firstStop = -1;
		int firstStopIndex = int.MaxValue;

		for (int i = 0; i < snippet.Length; i++) {
			char character = snippet[i];

			if (character == '\\' && i + 1 < snippet.Length && snippet[i + 1] is '$' or '}' or '\\') {
				text.Append(snippet[++i]);
				continue;
			}

			if (character != '$' || i + 1 >= snippet.Length) {
				text.Append(character);
				continue;
			}

			int index;
			string placeholder = string.Empty;

			if (char.IsAsciiDigit(snippet[i + 1])) {
				int end = i + 1;

				while (end < snippet.Length && char.IsAsciiDigit(snippet[end])) end++;

				index = int.Parse(snippet.AsSpan(i + 1, end - i - 1));
				i = end - 1;
			}
			else if (snippet[i + 1] == '{' && i + 2 < snippet.Length && char.IsAsciiDigit(snippet[i + 2])) {
				int end = i + 2;

				while (end < snippet.Length && char.IsAsciiDigit(snippet[end])) end++;

				index = int.Parse(snippet.AsSpan(i + 2, end - i - 2));

				if (end < snippet.Length && snippet[end] == ':') {
					int close = snippet.IndexOf('}', end);

					if (close < 0) close = snippet.Length;

					placeholder = snippet[(end + 1)..close];
					end = close;
				}

				i = Math.Min(end, snippet.Length - 1);
			}
			else {
				text.Append(character);
				continue;
			}

			if (index == 0) caret = text.Length;
			else if (index < firstStopIndex) {
				firstStopIndex = index;
				firstStop = text.Length;
			}

			text.Append(placeholder);
		}

		return (text.ToString(), firstStop >= 0 ? firstStop : caret >= 0 ? caret : text.Length);
	}
}
