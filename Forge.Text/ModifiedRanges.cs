using System.Runtime.InteropServices;

namespace Forge.Text;

public readonly record struct ModifiedRange(int StartLine, int EndLine, int EndColumn);

public static class ModifiedRanges
{
	public static List<ModifiedRange> Compute(string original, string modified) {
		List<int> starts = [0];

		for (int i = modified.IndexOf('\n'); i >= 0; i = modified.IndexOf('\n', i + 1)) starts.Add(i + 1);

		List<(int Start, int End)> lines = [];
		List<LineHunk> hunks = [];

		LineDiff.Compute(CollectionsMarshal.AsSpan(Hashes(original)), CollectionsMarshal.AsSpan(Hashes(modified)), hunks);

		foreach (LineHunk hunk in hunks) {
			int start = hunk.ModifiedCount == 0 ? Math.Max(1, hunk.ModifiedStart) : hunk.ModifiedStart + 1;
			int end = hunk.ModifiedCount == 0 ? start : hunk.ModifiedStart + hunk.ModifiedCount;

			start = Math.Min(start, starts.Count);
			end = Math.Min(end, starts.Count);

			if (lines.Count > 0 && start <= lines[^1].End) lines[^1] = (lines[^1].Start, Math.Max(lines[^1].End, end));
			else lines.Add((start, end));
		}

		List<ModifiedRange> ranges = [with(lines.Count)];

		foreach ((int start, int end) in lines) {
			int lineEnd = end < starts.Count ? starts[end] : modified.Length;

			while (lineEnd > starts[end - 1] && modified[lineEnd - 1] is '\n' or '\r') lineEnd--;

			ranges.Add(new ModifiedRange(start, end, lineEnd - starts[end - 1] + 1));
		}

		return ranges;
	}

	private static List<int> Hashes(ReadOnlySpan<char> text) {
		List<int> hashes = [];
		int start = 0;

		for (int i = 0; i < text.Length; i++) {
			if (text[i] is not ('\n' or '\r')) continue;

			hashes.Add(string.GetHashCode(text[start..i]));

			if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;

			start = i + 1;
		}

		hashes.Add(string.GetHashCode(text[start..]));
		return hashes;
	}
}
