using System.Buffers;

namespace Forge.Text;

public readonly record struct LineHunk(int OriginalStart, int OriginalCount, int ModifiedStart, int ModifiedCount);

public static class LineDiff
{
	private const int MaxCost = 1 << 22;

	public static List<LineHunk> Compute(ReadOnlySpan<char> original, ReadOnlySpan<char> modified) {
		List<LineHunk> hunks = [];
		int[] left = HashLines(original, out int leftCount);
		int[] right = HashLines(modified, out int rightCount);

		try {
			Compute(left.AsSpan(0, leftCount), right.AsSpan(0, rightCount), hunks);
		}
		finally {
			ArrayPool<int>.Shared.Return(left);
			ArrayPool<int>.Shared.Return(right);
		}

		return hunks;
	}

	public static void Compute(ReadOnlySpan<int> left, ReadOnlySpan<int> right, List<LineHunk> hunks) {
		int prefix = left.CommonPrefixLength(right);
		int suffix = 0;

		while (suffix < left.Length - prefix && suffix < right.Length - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;

		ReadOnlySpan<int> a = left[prefix..^suffix];
		ReadOnlySpan<int> b = right[prefix..^suffix];

		if (a.IsEmpty && b.IsEmpty) return;

		if (a.IsEmpty || b.IsEmpty || (long)(a.Length + b.Length) * Math.Min(a.Length, b.Length) > MaxCost) {
			hunks.Add(new LineHunk(prefix, a.Length, prefix, b.Length));
			return;
		}

		Myers(a, b, prefix, hunks);
	}

	private static void Myers(ReadOnlySpan<int> a, ReadOnlySpan<int> b, int offset, List<LineHunk> hunks) {
		int n = a.Length;
		int m = b.Length;
		int max = n + m;
		int width = (2 * max) + 2;
		int[] v = ArrayPool<int>.Shared.Rent(width);
		List<int[]> trace = [];

		try {
			Array.Clear(v, 0, width);
			int d;
			bool done = false;

			for (d = 0; d <= max && !done; d++) {
				int[] snapshot = ArrayPool<int>.Shared.Rent(width);
				Array.Copy(v, snapshot, width);
				trace.Add(snapshot);

				for (int k = -d; k <= d; k += 2) {
					int x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]) ? v[max + k + 1] : v[max + k - 1] + 1;
					int y = x - k;

					while (x < n && y < m && a[x] == b[y]) {
						x++;
						y++;
					}

					v[max + k] = x;

					if (x >= n && y >= m) {
						done = true;
						break;
					}
				}
			}

			Span<bool> deleted = n <= 1024 ? stackalloc bool[n] : new bool[n];
			Span<bool> inserted = m <= 1024 ? stackalloc bool[m] : new bool[m];
			deleted.Clear();
			inserted.Clear();

			int px = n;
			int py = m;

			for (int step = trace.Count - 1; step > 0; step--) {
				int[] previous = trace[step];
				int k = px - py;
				int previousK = k == -step || (k != step && previous[max + k - 1] < previous[max + k + 1]) ? k + 1 : k - 1;
				int previousX = previous[max + previousK];
				int previousY = previousX - previousK;

				while (px > previousX && py > previousY) {
					px--;
					py--;
				}

				if (previousK == k + 1) inserted[py - 1] = true;
				else deleted[px - 1] = true;

				px = previousX;
				py = previousY;
			}

			int i = 0;
			int j = 0;

			while (i < n || j < m) {
				if (i < n && j < m && !deleted[i] && !inserted[j]) {
					i++;
					j++;
					continue;
				}

				int startI = i;
				int startJ = j;

				while (i < n && deleted[i]) i++;
				while (j < m && inserted[j]) j++;

				hunks.Add(new LineHunk(offset + startI, i - startI, offset + startJ, j - startJ));
			}
		}
		finally {
			ArrayPool<int>.Shared.Return(v);

			foreach (int[] snapshot in trace) ArrayPool<int>.Shared.Return(snapshot);
		}
	}

	private static int[] HashLines(ReadOnlySpan<char> text, out int count) {
		int lines = text.Count('\n') + 1;
		int[] hashes = ArrayPool<int>.Shared.Rent(lines);
		count = 0;

		if (text.IsEmpty) return hashes;

		foreach (ReadOnlySpan<char> line in text.EnumerateLines()) hashes[count++] = string.GetHashCode(line.TrimEnd('\r'));

		if (count > 0 && text.Length > 0 && text[^1] == '\n') count--;

		return hashes;
	}
}
