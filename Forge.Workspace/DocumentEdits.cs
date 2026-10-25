using System.Buffers;

using Forge.Common.Commands;
using Forge.Text;

namespace Forge.Workspace;

public static class DocumentEdits
{
	public static void Apply(TextBuffer buffer, IReadOnlyList<HostTextEdit> edits, out int earliest, out int delta) {
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

			buffer.Replace(start, end - start, edit.NewText);

			earliest = Math.Min(earliest, start);
			delta += edit.NewText.Length - (end - start);
		}

		buffer.EndEdit();
		span.Clear();
		ArrayPool<HostTextEdit>.Shared.Return(ordered);
	}
}
