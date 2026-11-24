using Forge.Common.Commands;
using Forge.Text;

namespace Forge.Workspace;

public static class RangeFormatting
{
	public static async Task<List<HostTextEdit>> ComputeAsync(IReadOnlyList<ModifiedRange> modified, Func<ModifiedRange, Task<IReadOnlyList<HostTextEdit>>> compute) {
		List<ModifiedRange> ranges = [.. modified];
		List<IReadOnlyList<HostTextEdit>> edits = [with(ranges.Count)];

		foreach (ModifiedRange range in ranges) edits.Add(await compute(range).ConfigureAwait(false));

		for (int i = 0; i < ranges.Count; i++) {
			for (int j = i + 1; j < ranges.Count; j++) {
				if (!Intersecting(edits[i], edits[j])) continue;

				ModifiedRange merged = Plus(ranges[i], ranges[j]);
				IReadOnlyList<HostTextEdit> recomputed = await compute(merged).ConfigureAwait(false);

				ranges.RemoveAt(j);
				ranges.RemoveAt(i);
				ranges.Add(merged);
				edits.RemoveAt(j);
				edits.RemoveAt(i);
				edits.Add(recomputed);
				i = 0;
				j = 0;
			}
		}

		List<HostTextEdit> all = [];

		foreach (IReadOnlyList<HostTextEdit> list in edits) all.AddRange(list);

		return all;
	}

	private static ModifiedRange Plus(in ModifiedRange left, in ModifiedRange right) {
		int start = Math.Min(left.StartLine, right.StartLine);
		(int endLine, int endColumn) = left.EndLine > right.EndLine || (left.EndLine == right.EndLine && left.EndColumn >= right.EndColumn) ? (left.EndLine, left.EndColumn) : (right.EndLine, right.EndColumn);

		return new ModifiedRange(start, endLine, endColumn);
	}

	public static bool Intersecting(IReadOnlyList<HostTextEdit> left, IReadOnlyList<HostTextEdit> right) {
		if (left.Count == 0 || right.Count == 0) return false;

		foreach (HostTextEdit edit in left) {
			foreach (HostTextEdit other in right) {
				if (Intersects(edit, other)) return true;
			}
		}

		return false;
	}

	public static bool Intersects(in HostTextEdit left, in HostTextEdit right) {
		(int startLine, int startColumn) = Max((left.StartLine, left.StartColumn), (right.StartLine, right.StartColumn));
		(int endLine, int endColumn) = Min((left.EndLine, left.EndColumn), (right.EndLine, right.EndColumn));

		return startLine < endLine || (startLine == endLine && startColumn <= endColumn);
	}

	private static (int Line, int Column) Max((int Line, int Column) left, (int Line, int Column) right) => left.Line > right.Line || (left.Line == right.Line && left.Column >= right.Column) ? left : right;

	private static (int Line, int Column) Min((int Line, int Column) left, (int Line, int Column) right) => left.Line < right.Line || (left.Line == right.Line && left.Column <= right.Column) ? left : right;
}
