namespace Forge.Text;

public readonly record struct LineMove(int OriginalStart, int OriginalCount, int ModifiedStart, int ModifiedCount);

public static class MoveDetector
{
	public static List<LineMove> Compute(List<LineHunk> changes, string[] original, string[] modified) {
		List<LineMove> moves = [];
		HashSet<int> excluded = [];

		SimpleMoves(changes, original, modified, moves, excluded);

		List<LineHunk> remaining = [];

		for (int i = 0; i < changes.Count; i++) {
			if (!excluded.Contains(i)) remaining.Add(changes[i]);
		}

		UnchangedMoves(remaining, original, modified, moves);

		moves = JoinClose(moves);
		moves.RemoveAll(move => !LongEnough(move, original));
		moves.RemoveAll(move => InSameDiff(changes, move));
		return moves;
	}

	private static void SimpleMoves(List<LineHunk> changes, string[] original, string[] modified, List<LineMove> moves, HashSet<int> excluded) {
		List<int> insertions = [];

		for (int i = 0; i < changes.Count; i++) {
			if (changes[i].OriginalCount == 0 && changes[i].ModifiedCount >= 3) insertions.Add(i);
		}

		for (int i = 0; i < changes.Count; i++) {
			LineHunk deletion = changes[i];

			if (deletion.ModifiedCount != 0 || deletion.OriginalCount < 3) continue;

			Dictionary<char, int> left = Histogram(original, deletion.OriginalStart, deletion.OriginalCount, out int leftTotal);
			double best = -1;
			int bestIndex = -1;

			foreach (int candidate in insertions) {
				LineHunk insertion = changes[candidate];
				Dictionary<char, int> right = Histogram(modified, insertion.ModifiedStart, insertion.ModifiedCount, out int rightTotal);
				double similarity = Similarity(left, right, leftTotal + rightTotal);

				if (similarity <= best) continue;

				best = similarity;
				bestIndex = candidate;
			}

			if (best <= 0.90 || bestIndex < 0) continue;

			LineHunk match = changes[bestIndex];
			insertions.Remove(bestIndex);
			moves.Add(new LineMove(deletion.OriginalStart, deletion.OriginalCount, match.ModifiedStart, match.ModifiedCount));
			excluded.Add(i);
			excluded.Add(bestIndex);
		}
	}

	private static Dictionary<char, int> Histogram(string[] lines, int start, int count, out int total) {
		Dictionary<char, int> histogram = [];
		total = 0;

		for (int i = start; i < start + count; i++) {
			foreach (char character in lines[i]) {
				histogram[character] = histogram.GetValueOrDefault(character) + 1;
				total++;
			}

			histogram['\n'] = histogram.GetValueOrDefault('\n') + 1;
			total++;
		}

		return histogram;
	}

	private static double Similarity(Dictionary<char, int> left, Dictionary<char, int> right, int total) {
		int difference = 0;

		foreach ((char key, int value) in left) difference += Math.Abs(value - right.GetValueOrDefault(key));

		foreach ((char key, int value) in right) {
			if (!left.ContainsKey(key)) difference += value;
		}

		return total == 0 ? 0 : 1.0 - ((double)difference / total);
	}

	private sealed class Mapping(int originalStart, int originalEnd, int modifiedStart, int modifiedEnd)
	{
		public int OriginalStart = originalStart;
		public int OriginalEnd = originalEnd;
		public int ModifiedStart = modifiedStart;
		public int ModifiedEnd = modifiedEnd;
	}

	private static void UnchangedMoves(List<LineHunk> changes, string[] original, string[] modified, List<LineMove> moves) {
		int[] originalHashes = Hash(original);
		int[] modifiedHashes = Hash(modified);
		Dictionary<(int, int, int), List<int>> triples = [];

		foreach (LineHunk change in changes) {
			for (int i = change.OriginalStart; i < change.OriginalStart + change.OriginalCount - 2; i++) {
				(int, int, int) key = (originalHashes[i], originalHashes[i + 1], originalHashes[i + 2]);

				if (!triples.TryGetValue(key, out List<int>? starts)) triples[key] = starts = [];

				starts.Add(i);
			}
		}

		List<Mapping> possible = [];
		List<LineHunk> sorted = [.. changes];
		sorted.Sort(static (a, b) => a.ModifiedStart.CompareTo(b.ModifiedStart));

		foreach (LineHunk change in sorted) {
			List<Mapping> last = [];

			for (int i = change.ModifiedStart; i < change.ModifiedStart + change.ModifiedCount - 2; i++) {
				List<Mapping> next = [];

				if (triples.TryGetValue((modifiedHashes[i], modifiedHashes[i + 1], modifiedHashes[i + 2]), out List<int>? starts)) {
					foreach (int start in starts) {
						Mapping? extended = null;

						foreach (Mapping mapping in last) {
							if (mapping.OriginalEnd + 1 != start + 3 || mapping.ModifiedEnd + 1 != i + 3) continue;

							mapping.OriginalEnd = start + 3;
							mapping.ModifiedEnd = i + 3;
							extended = mapping;
							break;
						}

						if (extended is null) {
							extended = new Mapping(start, start + 3, i, i + 3);
							possible.Add(extended);
						}

						next.Add(extended);
					}
				}

				last = next;
			}
		}

		possible.Sort(static (a, b) => (b.ModifiedEnd - b.ModifiedStart).CompareTo(a.ModifiedEnd - a.ModifiedStart));

		bool[] modifiedUsed = new bool[modified.Length];
		bool[] originalUsed = new bool[original.Length];
		int first = moves.Count;

		foreach (Mapping mapping in possible) {
			int delta = mapping.ModifiedStart - mapping.OriginalStart;
			int run = -1;

			for (int line = mapping.ModifiedStart; line <= mapping.ModifiedEnd; line++) {
				bool free = line < mapping.ModifiedEnd && !modifiedUsed[line] && !originalUsed[line - delta];

				if (free) {
					if (run < 0) run = line;

					continue;
				}

				if (run >= 0 && line - run >= 3) {
					moves.Add(new LineMove(run - delta, line - run, run, line - run));

					for (int j = run; j < line; j++) {
						modifiedUsed[j] = true;
						originalUsed[j - delta] = true;
					}
				}

				run = -1;
			}
		}

		moves.Sort(first, moves.Count - first, Comparer<LineMove>.Create(static (a, b) => a.OriginalStart.CompareTo(b.OriginalStart)));

		for (int i = first; i < moves.Count; i++) {
			LineMove move = moves[i];
			LineHunk? aboveOriginal = LastWhere(changes, change => change.OriginalStart <= move.OriginalStart, true);
			LineHunk? aboveModified = LastWhere(sorted, change => change.ModifiedStart <= move.ModifiedStart, false);
			int linesAbove = Math.Max(move.OriginalStart - (aboveOriginal?.OriginalStart ?? move.OriginalStart), move.ModifiedStart - (aboveModified?.ModifiedStart ?? move.ModifiedStart));

			LineHunk? belowOriginal = LastWhere(changes, change => change.OriginalStart < move.OriginalStart + move.OriginalCount, true);
			LineHunk? belowModified = LastWhere(sorted, change => change.ModifiedStart < move.ModifiedStart + move.ModifiedCount, false);
			int linesBelow = Math.Max(
				(belowOriginal is { } bo ? bo.OriginalStart + bo.OriginalCount : 0) - (move.OriginalStart + move.OriginalCount),
				(belowModified is { } bm ? bm.ModifiedStart + bm.ModifiedCount : 0) - (move.ModifiedStart + move.ModifiedCount));

			int top = 0;

			for (; top < linesAbove; top++) {
				int originalLine = move.OriginalStart - top - 1;
				int modifiedLine = move.ModifiedStart - top - 1;

				if (originalLine < 0 || modifiedLine < 0 || modifiedUsed[modifiedLine] || originalUsed[originalLine] || !Similar(original[originalLine], modified[modifiedLine])) break;
			}

			for (int j = 0; j < top; j++) {
				originalUsed[move.OriginalStart - top + j] = true;
				modifiedUsed[move.ModifiedStart - top + j] = true;
			}

			int bottom = 0;

			for (; bottom < linesBelow; bottom++) {
				int originalLine = move.OriginalStart + move.OriginalCount + bottom;
				int modifiedLine = move.ModifiedStart + move.ModifiedCount + bottom;

				if (originalLine >= original.Length || modifiedLine >= modified.Length || modifiedUsed[modifiedLine] || originalUsed[originalLine] || !Similar(original[originalLine], modified[modifiedLine])) break;
			}

			for (int j = 0; j < bottom; j++) {
				originalUsed[move.OriginalStart + move.OriginalCount + j] = true;
				modifiedUsed[move.ModifiedStart + move.ModifiedCount + j] = true;
			}

			if (top > 0 || bottom > 0) moves[i] = new LineMove(move.OriginalStart - top, move.OriginalCount + top + bottom, move.ModifiedStart - top, move.ModifiedCount + top + bottom);
		}
	}

	private static LineHunk? LastWhere(List<LineHunk> changes, Func<LineHunk, bool> predicate, bool _) {
		LineHunk? found = null;

		foreach (LineHunk change in changes) {
			if (!predicate(change)) break;

			found = change;
		}

		return found;
	}

	private static int[] Hash(string[] lines) {
		int[] hashes = new int[lines.Length];

		for (int i = 0; i < lines.Length; i++) hashes[i] = string.GetHashCode(lines[i].AsSpan());

		return hashes;
	}

	private static bool Similar(string left, string right) {
		if (left.AsSpan().Trim().SequenceEqual(right.AsSpan().Trim())) return true;

		if (left.Length > 300 && right.Length > 300) return false;

		int[] a = new int[left.Length];
		int[] b = new int[right.Length];

		for (int i = 0; i < left.Length; i++) a[i] = left[i];
		for (int i = 0; i < right.Length; i++) b[i] = right[i];

		List<LineHunk> diffs = [];
		LineDiff.Compute(a, b, diffs);

		bool[] changed = new bool[left.Length];

		foreach (LineHunk diff in diffs) {
			for (int i = diff.OriginalStart; i < diff.OriginalStart + diff.OriginalCount; i++) changed[i] = true;
		}

		int common = 0;

		for (int i = 0; i < left.Length; i++) {
			if (!changed[i] && !char.IsWhiteSpace(left[i])) common++;
		}

		string longer = left.Length > right.Length ? left : right;
		int longest = 0;

		for (int i = 0; i < left.Length && i < longer.Length; i++) {
			if (!char.IsWhiteSpace(longer[i])) longest++;
		}

		return longest > 10 && (double)common / longest > 0.6;
	}

	private static List<LineMove> JoinClose(List<LineMove> moves) {
		if (moves.Count == 0) return moves;

		moves.Sort(static (a, b) => a.OriginalStart.CompareTo(b.OriginalStart));
		List<LineMove> result = [moves[0]];

		for (int i = 1; i < moves.Count; i++) {
			LineMove last = result[^1];
			LineMove current = moves[i];
			int originalDistance = current.OriginalStart - (last.OriginalStart + last.OriginalCount);
			int modifiedDistance = current.ModifiedStart - (last.ModifiedStart + last.ModifiedCount);

			if (originalDistance >= 0 && modifiedDistance >= 0 && originalDistance + modifiedDistance <= 2) {
				result[^1] = new LineMove(last.OriginalStart, current.OriginalStart + current.OriginalCount - last.OriginalStart, last.ModifiedStart, current.ModifiedStart + current.ModifiedCount - last.ModifiedStart);
				continue;
			}

			result.Add(current);
		}

		return result;
	}

	private static bool LongEnough(LineMove move, string[] original) {
		int length = 0;
		int substantial = 0;

		for (int i = move.OriginalStart; i < move.OriginalStart + move.OriginalCount; i++) {
			int trimmed = original[i].AsSpan().Trim().Length;
			length += trimmed + (i > move.OriginalStart ? 1 : 0);

			if (trimmed >= 2) substantial++;
		}

		return length >= 15 && substantial >= 2;
	}

	private static bool InSameDiff(List<LineHunk> changes, LineMove move) {
		int originalIndex = -1;
		int modifiedIndex = -1;

		for (int i = 0; i < changes.Count; i++) {
			if (changes[i].OriginalStart < move.OriginalStart + move.OriginalCount) originalIndex = i;
			if (changes[i].ModifiedStart < move.ModifiedStart + move.ModifiedCount) modifiedIndex = i;
		}

		return originalIndex >= 0 && originalIndex == modifiedIndex;
	}
}
