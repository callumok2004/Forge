namespace Forge.Search;

public static class WordFilter
{
	private const int StackLimit = 1024;

	public static bool Matches(ReadOnlySpan<char> word, ReadOnlySpan<char> target) => ContiguousSubstring(word, target) || Words(word, target);

	public static bool ContiguousSubstring(ReadOnlySpan<char> word, ReadOnlySpan<char> target) =>
		word.Length <= target.Length && target.Contains(word, StringComparison.OrdinalIgnoreCase);

	public static bool Words(ReadOnlySpan<char> word, ReadOnlySpan<char> target) {
		if (target.IsEmpty) return false;

		if (word.IsEmpty) return true;

		int size = (word.Length + 1) * (target.Length + 1);
		Span<sbyte> memo = size <= StackLimit ? stackalloc sbyte[size] : new sbyte[size];
		memo.Clear();

		for (int start = 0; start < target.Length; start = NextWord(target, start + 1)) {
			if (Match(word, target, 0, start, memo)) return true;
		}

		return false;
	}

	private static bool Match(ReadOnlySpan<char> word, ReadOnlySpan<char> target, int wordIndex, int targetIndex, Span<sbyte> memo) {
		if (wordIndex == word.Length) return true;

		if (targetIndex == target.Length) return false;

		int key = (wordIndex * (target.Length + 1)) + targetIndex;

		if (memo[key] != 0) return memo[key] > 0;

		bool result = Compute(word, target, wordIndex, targetIndex, memo);
		memo[key] = result ? (sbyte)1 : (sbyte)-1;
		return result;
	}

	private static bool Compute(ReadOnlySpan<char> word, ReadOnlySpan<char> target, int wordIndex, int targetIndex, Span<sbyte> memo) {
		if (!CharactersMatch(word[wordIndex], target[targetIndex])) return false;

		int next = targetIndex + 1;

		if (Match(word, target, wordIndex + 1, next, memo)) return true;

		while ((next = NextWord(target, next)) < target.Length) {
			if (Match(word, target, wordIndex + 1, next, memo)) return true;

			next++;
		}

		return false;
	}

	private static bool CharactersMatch(char left, char right) =>
		char.ToLowerInvariant(left) == char.ToLowerInvariant(right) || (IsWordSeparator(left) && IsWordSeparator(right));

	private static bool IsWordSeparator(char c) => char.IsWhiteSpace(c) || c is '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or '`' or '\'' or '"' or '-' or '/' or ';' or ':' or ',' or '.' or '?' or '!';

	private static int NextWord(ReadOnlySpan<char> text, int start) {
		for (int i = start; i < text.Length; i++) {
			if (IsWordSeparator(text[i]) || (i > 0 && IsWordSeparator(text[i - 1]))) return i;
		}

		return text.Length;
	}
}
