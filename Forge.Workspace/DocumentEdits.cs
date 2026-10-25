using Forge.Common.Commands;
using Forge.Text;

namespace Forge.Workspace;

public static class DocumentEdits
{
	public static void Apply(TextBuffer buffer, IReadOnlyList<HostTextEdit> edits, out int earliest, out int delta) {
		List<HostTextEdit> ordered = [.. edits];

		ordered.Sort(static (left, right) => right.StartLine != left.StartLine ? right.StartLine.CompareTo(left.StartLine) : right.StartColumn.CompareTo(left.StartColumn));

		earliest = int.MaxValue;
		delta = 0;

		buffer.BeginEdit();

		foreach (HostTextEdit edit in ordered) {
			TextSnapshot snapshot = buffer.CreateSnapshot();

			if (edit.StartLine < 1 || edit.StartLine - 1 >= snapshot.LineCount) continue;

			TextSpan line = snapshot.GetLineSpan(edit.StartLine - 1);
			TextSpan endLine = edit.EndLine >= 1 && edit.EndLine - 1 < snapshot.LineCount ? snapshot.GetLineSpan(edit.EndLine - 1) : line;
			int start = Math.Clamp(line.Start + edit.StartColumn - 1, line.Start, line.End);
			int end = Math.Clamp(endLine.Start + edit.EndColumn - 1, start, endLine.End);

			buffer.Replace(start, end - start, edit.NewText);

			earliest = Math.Min(earliest, start);
			delta += edit.NewText.Length - (end - start);
		}

		buffer.EndEdit();
	}
}
