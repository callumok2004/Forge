using Forge.Extensions.Dotnet.Server;

namespace Forge.Extensions.Dotnet.Language;

public static class CommentIndentFilter
{
	public static List<ServerTextEdit> Apply(string text, IReadOnlyList<ServerTextEdit> edits) {
		List<int> starts = [0];

		for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1)) starts.Add(i + 1);

		List<ServerTextEdit> result = [with(edits.Count)];

		foreach (ServerTextEdit edit in edits) {
			ReadOnlySpan<char> old = Slice(text, starts, edit);

			if (edit.StartLine != edit.EndLine) {
				if (edit.EndLine == edit.StartLine + 1 && BreakAndIndent(old, out ReadOnlySpan<char> _, out ReadOnlySpan<char> oldIndent) && BreakAndIndent(edit.NewText, out ReadOnlySpan<char> newBreak, out ReadOnlySpan<char> _) && edit.EndColumn - 1 == oldIndent.Length && StartsComment(text, starts, edit.EndLine)) result.Add(edit with { NewText = string.Concat(newBreak, oldIndent) });
				else result.Add(edit);

				continue;
			}

			if (IsIndent(edit.NewText) && IsIndent(old) && FirstNonWhitespace(text, starts, edit.StartLine) >= edit.EndColumn - 1 && StartsComment(text, starts, edit.StartLine)) continue;

			result.Add(edit);
		}

		return result;
	}

	private static ReadOnlySpan<char> Slice(string text, List<int> starts, ServerTextEdit edit) {
		int start = Offset(text, starts, edit.StartLine, edit.StartColumn);
		int end = Offset(text, starts, edit.EndLine, edit.EndColumn);

		return start < 0 || end < start ? [] : text.AsSpan(start, end - start);
	}

	private static int Offset(string text, List<int> starts, int line, int column) => line - 1 < 0 || line - 1 >= starts.Count ? -1 : Math.Min(starts[line - 1] + column - 1, text.Length);

	private static ReadOnlySpan<char> Line(string text, List<int> starts, int line) {
		if (line - 1 < 0 || line - 1 >= starts.Count) return [];

		int start = starts[line - 1];
		int end = line < starts.Count ? starts[line] : text.Length;

		return text.AsSpan(start, end - start).TrimEnd("\r\n");
	}

	private static int FirstNonWhitespace(string text, List<int> starts, int line) {
		ReadOnlySpan<char> content = Line(text, starts, line);
		int index = content.IndexOfAnyExcept(' ', '\t');

		return index < 0 ? content.Length : index;
	}

	private static bool StartsComment(string text, List<int> starts, int line) {
		ReadOnlySpan<char> content = Line(text, starts, line).TrimStart(" \t");

		return content.StartsWith("//") || content.StartsWith("/*") || content.StartsWith("*/") || content.StartsWith("*");
	}

	private static bool IsIndent(ReadOnlySpan<char> text) => text.IndexOfAnyExcept(' ', '\t') < 0;

	private static bool BreakAndIndent(ReadOnlySpan<char> text, out ReadOnlySpan<char> lineBreak, out ReadOnlySpan<char> indent) {
		int length = text.StartsWith("\r\n") ? 2 : text.StartsWith("\n") ? 1 : 0;

		lineBreak = text[..length];
		indent = text[length..];

		return length > 0 && IsIndent(indent);
	}
}
