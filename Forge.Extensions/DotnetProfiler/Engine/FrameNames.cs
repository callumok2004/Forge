using System.Text;

namespace Forge.Extensions.DotnetProfiler.Engine;

public static class FrameNames
{
	public static bool IsSystem(ReadOnlySpan<char> name, ReadOnlySpan<char> module) =>
		name.StartsWith("System.") || name.StartsWith("Microsoft.") || name.StartsWith("Interop") || name.StartsWith("0x") || module.StartsWith("System.");

	public static ReadOnlySpan<char> WithoutArguments(ReadOnlySpan<char> name) {
		int open = name.IndexOf('(');

		return open >= 0 ? name[..open] : name;
	}

	public static string Short(string name) {
		ReadOnlySpan<char> bare = WithoutArguments(name);
		int last = bare.LastIndexOf('.');

		if (last <= 0) return bare.Length == name.Length ? name : bare.ToString();

		int previous = bare[..last].LastIndexOf('.');

		return previous < 0 ? bare.ToString() : bare[(previous + 1)..].ToString();
	}

	public static string Display(string name, bool fullSignatures) {
		if (fullSignatures) return name;

		string shortName = Short(name);

		return name.Contains('(') ? string.Concat(shortName, "(…)") : shortName;
	}

	private static bool IsWord(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

	public static string Normalize(string name) {
		ReadOnlySpan<char> source = WithoutArguments(name);
		Span<char> clean = source.Length <= 512 ? stackalloc char[source.Length] : new char[source.Length];
		int length = 0;
		int depth = 0;

		for (int i = 0; i < source.Length; i++) {
			char c = source[i];

			if (c == '[') {
				depth++;
				continue;
			}

			if (c == ']') {
				depth = Math.Max(0, depth - 1);
				continue;
			}

			if (depth > 0) continue;

			if (c == '`' && i + 1 < source.Length && char.IsAsciiDigit(source[i + 1])) {
				while (i + 1 < source.Length && char.IsAsciiDigit(source[i + 1])) i++;

				continue;
			}

			clean[length++] = c;
		}

		ReadOnlySpan<char> text = clean[..length];

		if (!text.Contains('<')) return text.Length == name.Length ? name : text.ToString();

		StringBuilder output = new(text.Length);
		int at = 0;

		while (at < text.Length) {
			ReadOnlySpan<char> rest = text[at..];

			if (rest.StartsWith("+<>c")) {
				int after = at + 4;

				if (text[after..].StartsWith("__DisplayClass")) {
					after += 14;

					while (after < text.Length && IsWord(text[after])) after++;
				}

				if (Lambda(text, after, out string? lambda, out int end)) {
					output.Append('.').Append(lambda);
					at = end;
				}
				else at = after;

				continue;
			}

			if (rest.StartsWith(".<") && Lambda(text, at, out string? method, out int methodEnd)) {
				output.Append('.').Append(method);
				at = methodEnd;
				continue;
			}

			if (rest.StartsWith("+<") && StateMachine(text, at, out string? machine, out int machineEnd)) {
				output.Append('.').Append(machine);
				at = machineEnd;
				continue;
			}

			output.Append(text[at]);
			at++;
		}

		return output.ToString();
	}

	private static bool Name(ReadOnlySpan<char> text, int start, out string name, out int end) {
		end = start;

		while (end < text.Length && IsWord(text[end])) end++;

		name = text[start..end].ToString();
		return end > start && end < text.Length && text[end] == '>';
	}

	private static bool Lambda(ReadOnlySpan<char> text, int at, out string? method, out int end) {
		method = null;
		end = at;

		if (!text[at..].StartsWith(".<") || !Name(text, at + 2, out string name, out int close)) return false;

		int i = close + 1;
		bool local = text[i..].StartsWith("g__");

		if (!local && !text[i..].StartsWith("b__")) return false;

		i += 3;
		int start = i;

		while (i < text.Length && (IsWord(text[i]) || (local && text[i] == '|'))) i++;

		if (i == start) return false;

		method = name;
		end = i;
		return true;
	}

	private static bool StateMachine(ReadOnlySpan<char> text, int at, out string? method, out int end) {
		method = null;
		end = at;

		if (!Name(text, at + 2, out string name, out int close) || !text[(close + 1)..].StartsWith("d__")) return false;

		int i = close + 4;
		int digits = i;

		while (i < text.Length && char.IsAsciiDigit(text[i])) i++;

		if (i == digits || !text[i..].StartsWith(".MoveNext")) return false;

		method = name;
		end = i + 9;
		return true;
	}
}
