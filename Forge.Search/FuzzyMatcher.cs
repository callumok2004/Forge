namespace Forge.Search;

public readonly record struct FuzzyMatch(int Index, int Score);

public static class FuzzyMatcher
{
	private const int ConsecutiveBonus = 15;
	private const int WordBoundaryBonus = 20;
	private const int FileNameBonus = 12;
	private const int ExactCaseBonus = 4;
	private const int LeadingPenalty = -1;
	private const int MaxLeadingPenalty = -20;

	public static bool TryMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> candidate, out int score) {
		score = 0;

		if (pattern.IsEmpty) return true;

		if (candidate.IsEmpty || pattern.Length > candidate.Length) return false;

		int nameStart = candidate.LastIndexOfAny('/', '\\') + 1;

		int patternIndex = 0;
		int previousMatch = -1;
		int total = 0;

		for (int i = 0; i < candidate.Length && patternIndex < pattern.Length; i++) {
			char candidateChar = candidate[i];
			char patternChar = pattern[patternIndex];

			if (char.ToLowerInvariant(candidateChar) != char.ToLowerInvariant(patternChar)) continue;

			int characterScore = 1;

			if (previousMatch == i - 1 && previousMatch >= 0) characterScore += ConsecutiveBonus;

			if (IsWordBoundary(candidate, i)) characterScore += WordBoundaryBonus;

			if (i >= nameStart) characterScore += FileNameBonus;

			if (candidateChar == patternChar) characterScore += ExactCaseBonus;

			if (patternIndex == 0) characterScore += Math.Max(MaxLeadingPenalty, i * LeadingPenalty);

			total += characterScore;
			previousMatch = i;
			patternIndex++;
		}

		if (patternIndex < pattern.Length) return false;

		score = total - (candidate.Length / 8);
		return true;
	}

	private static bool IsWordBoundary(ReadOnlySpan<char> text, int index) {
		if (index == 0) return true;

		char previous = text[index - 1];
		char current = text[index];

		if (previous is '/' or '\\' or '_' or '-' or '.' or ' ') return true;

		return char.IsLower(previous) && char.IsUpper(current);
	}

	public static int Rank(ReadOnlySpan<char> pattern, string[] candidates, Span<FuzzyMatch> results) {
		int count = 0;
		int worstKeptScore = int.MinValue;

		for (int i = 0; i < candidates.Length; i++) {
			if (!TryMatch(pattern, candidates[i], out int score)) continue;

			if (count < results.Length) {
				results[count++] = new FuzzyMatch(i, score);

				if (count == results.Length) {
					SortDescending(results[..count]);
					worstKeptScore = results[count - 1].Score;
				}

				continue;
			}

			if (score <= worstKeptScore) continue;

			results[^1] = new FuzzyMatch(i, score);
			SortDescending(results);
			worstKeptScore = results[^1].Score;
		}

		if (count < results.Length) SortDescending(results[..count]);

		return count;
	}

	private static void SortDescending(Span<FuzzyMatch> matches) {
		for (int i = 1; i < matches.Length; i++) {
			FuzzyMatch current = matches[i];
			int j = i - 1;

			while (j >= 0 && matches[j].Score < current.Score) {
				matches[j + 1] = matches[j];
				j--;
			}

			matches[j + 1] = current;
		}
	}
}
