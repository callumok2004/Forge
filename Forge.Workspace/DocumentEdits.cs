using System.Buffers;

using Forge.Common.Commands;
using Forge.Text;

namespace Forge.Workspace;

public static class DocumentEdits
{
	public static void Apply(TextBuffer buffer, IReadOnlyList<HostTextEdit> edits, out int earliest, out int delta) => Apply(buffer, edits, [], false, out earliest, out delta);

	public static void Apply(TextBuffer buffer, IReadOnlyList<HostTextEdit> edits, Span<int> tracked, bool minimal, out int earliest, out int delta) {
		earliest = int.MaxValue;
		delta = 0;

		if (edits.Count == 0) return;

		HostTextEdit[] ordered = ArrayPool<HostTextEdit>.Shared.Rent(edits.Count);
		Span<HostTextEdit> span = ordered.AsSpan(0, edits.Count);

		for (int i = 0; i < span.Length; i++) span[i] = edits[i];

		span.Sort(static (left, right) => right.StartLine != left.StartLine ? right.StartLine.CompareTo(left.StartLine) : right.StartColumn.CompareTo(left.StartColumn));

		buffer.BeginEdit();

		foreach (HostTextEdit edit in span) {
			int lineCount = buffer.LineCount;

			if (edit.StartLine < 1 || edit.StartLine - 1 >= lineCount) continue;

			int lineStart = buffer.GetLineStartOffset(edit.StartLine - 1);
			int lineEnd = lineStart + buffer.GetLineLength(edit.StartLine - 1);
			bool validEnd = edit.EndLine >= 1 && edit.EndLine - 1 < lineCount;
			int endLineStart = validEnd ? buffer.GetLineStartOffset(edit.EndLine - 1) : lineStart;
			int endLineEnd = validEnd ? endLineStart + buffer.GetLineLength(edit.EndLine - 1) : lineEnd;
			int start = Math.Clamp(lineStart + edit.StartColumn - 1, lineStart, lineEnd);
			int end = Math.Clamp(endLineStart + edit.EndColumn - 1, start, endLineEnd);
			string text = edit.NewText;
			int length = end - start;

			if (!minimal || length > DiffLimit || text.Length > DiffLimit) {
				if (length == 0 && text.Length == 0) continue;

				Replace(buffer, start, length, text, tracked, ref earliest, ref delta);
				continue;
			}

			char[] rented = ArrayPool<char>.Shared.Rent(Math.Max(length, 1));
			ReadOnlySpan<char> original = rented.AsSpan(0, length);
			buffer.CopyTo(start, length, rented.AsSpan(0, length));
			List<CharChange> changes = CharEdits.Compute(original, text);
			ArrayPool<char>.Shared.Return(rented);

			for (int i = changes.Count - 1; i >= 0; i--) {
				CharChange change = changes[i];
				Replace(buffer, start + change.OriginalStart, change.OriginalLength, text.AsSpan(change.ModifiedStart, change.ModifiedLength), tracked, ref earliest, ref delta);
			}
		}

		buffer.EndEdit();
		span.Clear();
		ArrayPool<HostTextEdit>.Shared.Return(ordered);
	}

	private const int DiffLimit = 100_000;

	private static void Replace(TextBuffer buffer, int start, int length, ReadOnlySpan<char> text, Span<int> tracked, ref int earliest, ref int delta) {
		int end = start + length;

		buffer.Replace(start, length, text);

		int change = text.Length - length;

		for (int i = 0; i < tracked.Length; i++) {
			if (tracked[i] > end) tracked[i] += change;
			else if (tracked[i] >= start) tracked[i] = start + text.Length;
		}

		earliest = Math.Min(earliest, start);
		delta += change;
	}
}
