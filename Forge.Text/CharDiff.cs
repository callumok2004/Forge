using System.Buffers;

namespace Forge.Text;

public readonly record struct CharRange(int Line, int Start, int End);

public static class CharDiff
{
	private const int MaxChars = 1 << 15;

	public static void Compute(ReadOnlySpan<char> original, int originalLine, ReadOnlySpan<char> modified, int modifiedLine, List<CharRange> originalRanges, List<CharRange> modifiedRanges) {
		if (original.Length + modified.Length > MaxChars) return;

		int[] left = ArrayPool<int>.Shared.Rent(Math.Max(1, original.Length));
		int[] right = ArrayPool<int>.Shared.Rent(Math.Max(1, modified.Length));
		List<LineHunk> hunks = [];

		try {
			for (int i = 0; i < original.Length; i++) left[i] = original[i];
			for (int i = 0; i < modified.Length; i++) right[i] = modified[i];

			LineDiff.Compute(left.AsSpan(0, original.Length), right.AsSpan(0, modified.Length), hunks);
		}
		finally {
			ArrayPool<int>.Shared.Return(left);
			ArrayPool<int>.Shared.Return(right);
		}

		JoinShortMatches(hunks);

		foreach (LineHunk hunk in hunks) {
			Emit(original, originalLine, hunk.OriginalStart, hunk.OriginalCount, originalRanges);
			Emit(modified, modifiedLine, hunk.ModifiedStart, hunk.ModifiedCount, modifiedRanges);
		}
	}

	private static void JoinShortMatches(List<LineHunk> hunks) {
		int write = 0;

		for (int read = 0; read < hunks.Count; read++) {
			LineHunk current = hunks[read];

			if (write > 0) {
				LineHunk previous = hunks[write - 1];
				int originalGap = current.OriginalStart - (previous.OriginalStart + previous.OriginalCount);
				int modifiedGap = current.ModifiedStart - (previous.ModifiedStart + previous.ModifiedCount);

				if (originalGap <= 2 || modifiedGap <= 2) {
					hunks[write - 1] = new LineHunk(previous.OriginalStart, current.OriginalStart + current.OriginalCount - previous.OriginalStart, previous.ModifiedStart, current.ModifiedStart + current.ModifiedCount - previous.ModifiedStart);
					continue;
				}
			}

			hunks[write++] = current;
		}

		hunks.RemoveRange(write, hunks.Count - write);
	}

	private static void Emit(ReadOnlySpan<char> text, int firstLine, int start, int count, List<CharRange> ranges) {
		int line = firstLine;
		int lineStart = 0;

		for (int i = 0; i < start; i++) {
			if (text[i] != '\n') continue;

			line++;
			lineStart = i + 1;
		}

		if (count == 0) {
			ranges.Add(new CharRange(line, start - lineStart, start - lineStart));
			return;
		}

		int end = start + count;
		int position = start;

		while (position < end) {
			int newline = text[position..end].IndexOf('\n');
			int stop = newline < 0 ? end : position + newline;

			ranges.Add(new CharRange(line, position - lineStart, newline < 0 ? stop - lineStart : int.MaxValue));

			if (newline < 0) break;

			position = stop + 1;
			lineStart = position;
			line++;
		}
	}
}
