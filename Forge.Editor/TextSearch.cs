using System.Buffers;

using Forge.Text;

namespace Forge.Editor;

public static class TextSearch
{
	private const int StackLimit = 512;

	public static int Next(TextSnapshot snapshot, ReadOnlySpan<char> query, int from, bool matchCase) =>
		Search(snapshot, query, from, matchCase, forward: true);

	public static int Previous(TextSnapshot snapshot, ReadOnlySpan<char> query, int from, bool matchCase) =>
		Search(snapshot, query, from, matchCase, forward: false);

	public static int Count(TextSnapshot snapshot, ReadOnlySpan<char> query, bool matchCase) {
		if (query.IsEmpty) return 0;

		StringComparison comparison = Comparison(matchCase);
		int total = 0;

		for (int line = 0; line < snapshot.LineCount; line++) {
			char[]? rented = null;
			Span<char> buffer = Rent(snapshot, line, ref rented, out int length);

			try {
				ReadOnlySpan<char> text = buffer[..length];
				int cursor = 0;

				while (cursor <= text.Length - query.Length) {
					int hit = text[cursor..].IndexOf(query, comparison);

					if (hit < 0) break;

					total++;

					cursor += hit + 1;
				}
			}
			finally {
				Return(rented);
			}
		}

		return total;
	}

	public static int RankOf(TextSnapshot snapshot, ReadOnlySpan<char> query, int offset, bool matchCase) {
		if (query.IsEmpty || offset < 0) return 0;

		StringComparison comparison = Comparison(matchCase);
		int rank = 0;

		for (int line = 0; line < snapshot.LineCount; line++) {
			int lineStart = snapshot.GetLineStartOffset(line);

			char[]? rented = null;
			Span<char> buffer = Rent(snapshot, line, ref rented, out int length);

			try {
				ReadOnlySpan<char> text = buffer[..length];
				int cursor = 0;

				while (cursor <= text.Length - query.Length) {
					int hit = text[cursor..].IndexOf(query, comparison);

					if (hit < 0) break;

					rank++;

					if (lineStart + cursor + hit == offset) return rank;

					cursor += hit + 1;
				}
			}
			finally {
				Return(rented);
			}
		}

		return 0;
	}

	private static int Search(TextSnapshot snapshot, ReadOnlySpan<char> query, int from, bool matchCase,
		bool forward) {
		if (query.IsEmpty || snapshot.Length == 0) return -1;

		StringComparison comparison = Comparison(matchCase);
		int lineCount = snapshot.LineCount;
		int startLine = Math.Clamp(snapshot.GetLineAt(Math.Clamp(from, 0, snapshot.Length)), 0, lineCount - 1);

		for (int step = 0; step <= lineCount; step++) {
			int line = forward
				? (startLine + step) % lineCount
				: ((startLine - step) % lineCount + lineCount) % lineCount;

			int lineStart = snapshot.GetLineStartOffset(line);

			char[]? rented = null;
			Span<char> buffer = Rent(snapshot, line, ref rented, out int length);

			try {
				ReadOnlySpan<char> text = buffer[..length];

				int hit = forward
					? FirstAtOrAfter(text, query, step == 0 ? from - lineStart : 0, comparison)
					: LastBefore(text, query, step == 0 ? from - lineStart : text.Length, comparison);

				if (hit >= 0) return lineStart + hit;
			}
			finally {
				Return(rented);
			}
		}

		return -1;
	}

	private static int FirstAtOrAfter(ReadOnlySpan<char> text, ReadOnlySpan<char> query, int start,
		StringComparison comparison) {
		start = Math.Max(0, start);

		if (start > text.Length - query.Length) return -1;

		int hit = text[start..].IndexOf(query, comparison);
		return hit < 0 ? -1 : start + hit;
	}

	private static int LastBefore(ReadOnlySpan<char> text, ReadOnlySpan<char> query, int limit,
		StringComparison comparison) {
		limit = Math.Min(limit, text.Length);

		if (limit <= 0) return -1;

		int best = -1;
		int cursor = 0;

		while (cursor <= limit - query.Length) {
			int hit = text[cursor..].IndexOf(query, comparison);

			if (hit < 0 || cursor + hit >= limit) break;

			best = cursor + hit;
			cursor = best + 1;
		}

		return best;
	}

	private static StringComparison Comparison(bool matchCase) =>
		matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

	private static Span<char> Rent(TextSnapshot snapshot, int line, ref char[]? rented, out int length) {
		rented = ArrayPool<char>.Shared.Rent(Math.Max(StackLimit, snapshot.GetLineLength(line)));

		length = snapshot.CopyLineTo(line, rented);
		return rented;
	}

	private static void Return(char[]? rented) {
		if (rented is not null) ArrayPool<char>.Shared.Return(rented);
	}
}
