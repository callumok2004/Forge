namespace Forge.Text;

public readonly record struct CharChange(int OriginalStart, int OriginalLength, int ModifiedStart, int ModifiedLength);

public static class CharEdits
{
	private const int MaxDifferences = 1447;

	public static List<CharChange> Compute(ReadOnlySpan<char> original, ReadOnlySpan<char> modified) {
		List<CharChange> changes = [];
		int prefix = original.CommonPrefixLength(modified);

		if (prefix == original.Length && prefix == modified.Length) return changes;

		int suffix = 0;
		int limit = Math.Min(original.Length, modified.Length) - prefix;

		while (suffix < limit && original[original.Length - 1 - suffix] == modified[modified.Length - 1 - suffix]) suffix++;

		ReadOnlySpan<char> a = original[prefix..(original.Length - suffix)];
		ReadOnlySpan<char> b = modified[prefix..(modified.Length - suffix)];

		if (a.IsEmpty || b.IsEmpty || !Myers(a, b, prefix, changes)) {
			changes.Clear();
			changes.Add(new CharChange(prefix, a.Length, prefix, b.Length));
		}

		return changes;
	}

	private static bool Myers(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int offset, List<CharChange> changes) {
		int n = a.Length;
		int m = b.Length;
		int max = Math.Min(n + m, MaxDifferences);
		int size = (2 * max) + 1;
		int[] v = new int[size];
		List<int[]> trace = [];
		int found = -1;

		for (int d = 0; d <= max && found < 0; d++) {
			trace.Add((int[])v.Clone());

			for (int k = -d; k <= d; k += 2) {
				int x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]) ? v[max + k + 1] : v[max + k - 1] + 1;
				int y = x - k;

				while (x < n && y < m && a[x] == b[y]) {
					x++;
					y++;
				}

				v[max + k] = x;

				if (x >= n && y >= m) {
					found = d;
					break;
				}
			}
		}

		if (found < 0) return false;

		List<(int X, int Y)> path = [];
		int px = n;
		int py = m;

		for (int d = found; d > 0; d--) {
			int[] previous = trace[d];
			int k = px - py;
			int previousK = k == -d || (k != d && previous[max + k - 1] < previous[max + k + 1]) ? k + 1 : k - 1;
			int previousX = previous[max + previousK];
			int previousY = previousX - previousK;

			while (px > previousX && py > previousY) {
				px--;
				py--;
			}

			path.Add((px, py));
			px = previousX;
			py = previousY;
		}

		path.Reverse();

		int x0 = 0;
		int y0 = 0;
		int pendingA = -1;
		int pendingB = -1;
		int lengthA = 0;
		int lengthB = 0;

		foreach ((int sx, int sy) in path) {
			int skip = Math.Min(sx - x0, sy - y0);

			if (skip > 0 && pendingA >= 0) {
				changes.Add(new CharChange(offset + pendingA, lengthA, offset + pendingB, lengthB));
				pendingA = -1;
			}

			x0 += skip;
			y0 += skip;

			if (pendingA < 0) {
				pendingA = x0;
				pendingB = y0;
				lengthA = 0;
				lengthB = 0;
			}

			if (sx > x0) {
				lengthA++;
				x0++;
			}
			else {
				lengthB++;
				y0++;
			}
		}

		if (pendingA >= 0) changes.Add(new CharChange(offset + pendingA, lengthA, offset + pendingB, lengthB));

		return true;
	}
}
