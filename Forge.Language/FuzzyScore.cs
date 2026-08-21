using Forge.Editor;

namespace Forge.Language;

public static class FuzzyScore
{
	private const int MaxLength = 128;

	private const int Diag = 1;
	private const int Left = 2;
	private const int LeftLeft = 3;

	[ThreadStatic]
	private static int[]? _table;

	[ThreadStatic]
	private static int[]? _diagonal;

	[ThreadStatic]
	private static int[]? _arrows;

	[ThreadStatic]
	private static int[]? _minWordMatchPos;

	[ThreadStatic]
	private static int[]? _maxWordMatchPos;

	private const int Stride = MaxLength + 1;

	public static bool TryScore(string pattern, string word, out int score, out int[] matches) {
		score = 0;
		matches = [];

		int patternLength = Math.Min(pattern.Length, MaxLength);
		int wordLength = Math.Min(word.Length, MaxLength);

		if (patternLength == 0 || wordLength == 0 || patternLength > wordLength) return false;

		string patternLow = pattern.ToLowerInvariant();
		string wordLow = word.ToLowerInvariant();

		int[] table = _table ??= new int[Stride * Stride];
		int[] diagonal = _diagonal ??= new int[Stride * Stride];
		int[] arrows = _arrows ??= new int[Stride * Stride];
		int[] minWordMatchPos = _minWordMatchPos ??= new int[(2 * MaxLength) + 1];
		int[] maxWordMatchPos = _maxWordMatchPos ??= new int[(2 * MaxLength) + 1];

		if (!IsPatternInWord(patternLow, patternLength, wordLow, wordLength, minWordMatchPos)) return false;

		FillMaxWordMatchPos(patternLength, wordLength, patternLow, wordLow, maxWordMatchPos);

		for (int row = 0; row <= patternLength; row++) {
			Array.Clear(table, row * Stride, wordLength + 1);
			Array.Clear(diagonal, row * Stride, wordLength + 1);
			Array.Clear(arrows, row * Stride, wordLength + 1);
		}

		bool hasStrongFirstMatch = false;
		int lastRow = 0;
		int lastColumn = 0;

		for (int row = 1, patternPos = 0; patternPos < patternLength; row++, patternPos++) {
			int minMatch = minWordMatchPos[patternPos];
			int maxMatch = maxWordMatchPos[patternPos];
			int nextMaxMatch = patternPos + 1 < patternLength
				? maxWordMatchPos[patternPos + 1]
				: wordLength;

			for (int column = minMatch + 1, wordPos = minMatch; wordPos < nextMaxMatch; column++, wordPos++) {
				int cellScore = int.MinValue;
				bool canComeDiag = false;

				if (wordPos <= maxMatch) {
					cellScore = DoScore(pattern, patternLow, patternPos, word, wordLow, wordPos,
						wordLength, diagonal[((row - 1) * Stride) + column - 1] == 0,
						ref hasStrongFirstMatch);
				}

				int diagScore = 0;

				if (cellScore != int.MinValue) {
					canComeDiag = true;
					diagScore = cellScore + table[((row - 1) * Stride) + column - 1];
				}

				bool canComeLeft = wordPos > minMatch;
				int leftScore = canComeLeft
					? table[(row * Stride) + column - 1] +
						(diagonal[(row * Stride) + column - 1] > 0 ? -5 : 0)
					: 0;

				bool canComeLeftLeft = wordPos > minMatch + 1 &&
					diagonal[(row * Stride) + column - 1] > 0;

				int leftLeftScore = canComeLeftLeft
					? table[(row * Stride) + column - 2] +
						(diagonal[(row * Stride) + column - 2] > 0 ? -5 : 0)
					: 0;

				if (canComeLeftLeft && (!canComeLeft || leftLeftScore >= leftScore) &&
					(!canComeDiag || leftLeftScore >= diagScore)) {
					table[(row * Stride) + column] = leftLeftScore;
					arrows[(row * Stride) + column] = LeftLeft;
					diagonal[(row * Stride) + column] = 0;
				}
				else if (canComeLeft && (!canComeDiag || leftScore >= diagScore)) {
					table[(row * Stride) + column] = leftScore;
					arrows[(row * Stride) + column] = Left;
					diagonal[(row * Stride) + column] = 0;
				}
				else if (canComeDiag) {
					table[(row * Stride) + column] = diagScore;
					arrows[(row * Stride) + column] = Diag;
					diagonal[(row * Stride) + column] = diagonal[((row - 1) * Stride) + column - 1] + 1;
				}
				else return false;

				lastRow = row;
				lastColumn = column;
			}
		}

		if (!hasStrongFirstMatch) return false;

		score = table[(lastRow * Stride) + lastColumn];

		int currentRow = lastRow;
		int currentColumn = lastColumn;
		int backwardsDiagLength = 0;
		int maxMatchColumn = 0;

		List<int> positions = [];

		while (currentRow >= 1) {
			int diagColumn = currentColumn;

			while (diagColumn >= 1) {
				int arrow = arrows[(currentRow * Stride) + diagColumn];

				if (arrow == LeftLeft) diagColumn -= 2;
				else if (arrow == Left) diagColumn--;
				else break;
			}

			if (backwardsDiagLength > 1 &&
				patternLow[currentRow - 1] == wordLow[currentColumn - 1] &&
				!IsUpperCaseAt(word, wordLow, diagColumn - 1) &&
				backwardsDiagLength + 1 > diagonal[(currentRow * Stride) + diagColumn]) diagColumn = currentColumn;

			backwardsDiagLength = diagColumn == currentColumn ? backwardsDiagLength + 1 : 1;

			if (maxMatchColumn == 0) maxMatchColumn = diagColumn;

			currentRow--;
			currentColumn = diagColumn - 1;
			positions.Add(currentColumn);
		}

		if (wordLength == patternLength) score += 2;

		score -= maxMatchColumn - patternLength;

		positions.Reverse();
		matches = [.. positions];

		return true;
	}

	private static bool IsPatternInWord(string patternLow, int patternLength, string wordLow,
		int wordLength, int[] minWordMatchPos) {
		int patternPos = 0;
		int wordPos = 0;

		while (patternPos < patternLength && wordPos < wordLength) {
			if (patternLow[patternPos] == wordLow[wordPos]) {
				minWordMatchPos[patternPos] = wordPos;
				patternPos++;
			}

			wordPos++;
		}

		return patternPos == patternLength;
	}

	private static void FillMaxWordMatchPos(int patternLength, int wordLength, string patternLow,
		string wordLow, int[] maxWordMatchPos) {
		int patternPos = patternLength - 1;
		int wordPos = wordLength - 1;

		while (patternPos >= 0 && wordPos >= 0) {
			if (patternLow[patternPos] == wordLow[wordPos]) {
				maxWordMatchPos[patternPos] = wordPos;
				patternPos--;
			}

			wordPos--;
		}
	}

	private static int DoScore(string pattern, string patternLow, int patternPos, string word,
		string wordLow, int wordPos, int wordLength, bool newMatchStart, ref bool firstMatchStrong) {
		if (patternLow[patternPos] != wordLow[wordPos]) return int.MinValue;

		int score = 1;
		bool isGapLocation = false;

		if (wordPos == patternPos) score = pattern[patternPos] == word[wordPos] ? 7 : 5;
		else if (IsUpperCaseAt(word, wordLow, wordPos) &&
			(wordPos == 0 || !IsUpperCaseAt(word, wordLow, wordPos - 1))) {
			score = pattern[patternPos] == word[wordPos] ? 7 : 5;
			isGapLocation = true;
		}
		else if (IsSeparatorAt(wordLow, wordPos) && (wordPos == 0 || !IsSeparatorAt(wordLow, wordPos - 1))) {
			score = 5;
		}
		else if (IsSeparatorAt(wordLow, wordPos - 1) || IsWhitespaceAt(wordLow, wordPos - 1)) {
			score = 5;
			isGapLocation = true;
		}

		if (score > 1 && patternPos == 0) firstMatchStrong = true;

		if (!isGapLocation) {
			isGapLocation = IsUpperCaseAt(word, wordLow, wordPos) ||
				IsSeparatorAt(wordLow, wordPos - 1) || IsWhitespaceAt(wordLow, wordPos - 1);
		}

		if (patternPos == 0) {
			if (wordPos > 0) score -= isGapLocation ? 3 : 5;
		}
		else if (newMatchStart) score += isGapLocation ? 2 : 0;
		else score += isGapLocation ? 0 : 1;

		if (wordPos + 1 == wordLength) score -= isGapLocation ? 3 : 5;

		return score;
	}

	private static bool IsUpperCaseAt(string word, string wordLow, int index) =>
		index >= 0 && index < word.Length && word[index] != wordLow[index];

	private static bool IsSeparatorAt(string value, int index) =>
		index >= 0 && index < value.Length && value[index] is
			'_' or '-' or '.' or ' ' or '/' or '\\' or '\'' or '"' or ':' or '$' or '<' or '>' or
			'(' or ')' or '[' or ']' or '{' or '}';

	private static bool IsWhitespaceAt(string value, int index) =>
		index >= 0 && index < value.Length && value[index] is ' ' or '\t';
}
