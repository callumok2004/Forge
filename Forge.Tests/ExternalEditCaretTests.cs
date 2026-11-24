using Forge.Common.Commands;
using Forge.Editor;
using Forge.Text;
using Forge.Workspace;

namespace Forge.Tests;

public class ExternalEditCaretTests
{
	private static (TextBuffer Buffer, TextView View) Open(string text, int caret, int? anchor = null) {
		TextBuffer buffer = new(text);
		TextView view = new(buffer);
		view.Select(anchor ?? caret, caret);
		return (buffer, view);
	}

	private static void ApplyAsFormat(TextBuffer buffer, TextView view, params HostTextEdit[] edits) {
		Span<int> tracked = [view.SelectionAnchor, view.CaretOffset];

		buffer.PushStackElement();
		DocumentEdits.Apply(buffer, edits, tracked, minimal: true, out int earliest, out _);
		buffer.PushStackElement();

		if (earliest == int.MaxValue) return;

		view.ExternalEdit(earliest);
		view.Select(tracked[0], tracked[1]);
	}

	private static void ApplyPlain(TextBuffer buffer, TextView view, params HostTextEdit[] edits) {
		DocumentEdits.Apply(buffer, edits, out int earliest, out _);

		if (earliest != int.MaxValue) view.ExternalEdit(earliest);
	}

	[Fact]
	public void EditsAboveAndBelowShiftCaretOnce() {
		(TextBuffer buffer, TextView view) = Open("a\nb\nc\nd", 4);

		ApplyPlain(buffer, view, new HostTextEdit(1, 1, 1, 1, "xx"), new HostTextEdit(4, 1, 4, 1, "yyyy"));

		Assert.Equal("xxa\nb\nc\nyyyyd", buffer.GetText());
		Assert.Equal(6, view.CaretOffset);
	}

	[Fact]
	public void EditsOnlyBelowCaretDoNotMoveIt() {
		(TextBuffer buffer, TextView view) = Open("one\ntwo\nthree", 2);

		ApplyPlain(buffer, view, new HostTextEdit(2, 1, 3, 6, "replaced"));

		Assert.Equal(2, view.CaretOffset);
	}

	[Fact]
	public void ManyLineInsertionsAboveCaretKeepItOnTheSameText() {
		string text = string.Join('\n', Enumerable.Range(0, 50).Select(static i => $"line{i}"));
		(TextBuffer buffer, TextView view) = Open(text, text.IndexOf("line40", StringComparison.Ordinal) + 2);
		HostTextEdit[] edits = [.. Enumerable.Range(1, 39).Select(static line => new HostTextEdit(line, 1, line, 1, "    "))];

		ApplyPlain(buffer, view, edits);

		Assert.Equal("ne40", buffer.GetText().Substring(view.CaretOffset, 4));
	}

	[Fact]
	public void SelectionSurvivesEditsOnBothSides() {
		const string text = "aaa\nSELECTED\nbbb";
		int start = text.IndexOf('S');
		(TextBuffer buffer, TextView view) = Open(text, start + 8, start);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 1, 1, 1, "  "), new HostTextEdit(3, 1, 3, 1, "  "));

		Assert.Equal("SELECTED", buffer.GetText(view.Selection));
	}

	[Fact]
	public void FormatIndentMovesCaretAtLineStartPastIndent() {
		(TextBuffer buffer, TextView view) = Open("{\nx;\n}", 2);

		ApplyAsFormat(buffer, view, new HostTextEdit(2, 1, 2, 1, "    "));

		Assert.Equal("{\n    x;\n}", buffer.GetText());
		Assert.Equal(6, view.CaretOffset);
	}

	[Fact]
	public void FormatWholeDocumentReplaceKeepsCaretOnSameCode() {
		const string before = "class A{\nvoid M(){\nint x=1;\n}\n}";
		const string after = "class A {\n    void M() {\n        int x = 1;\n    }\n}";
		int caret = before.IndexOf("x=1", StringComparison.Ordinal);
		(TextBuffer buffer, TextView view) = Open(before, caret);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 1, 5, 2, after));

		Assert.Equal(after, buffer.GetText());
		Assert.Equal(after.IndexOf("x = 1", StringComparison.Ordinal), view.CaretOffset);
	}

	[Fact]
	public void FormatUndoIsOneStepAndRestoresCaret() {
		const string before = "if(a){\nb();\n}";
		(TextBuffer buffer, TextView view) = Open(before, 9);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 3, 1, 3, " "), new HostTextEdit(1, 6, 1, 6, " "), new HostTextEdit(2, 1, 2, 1, "    "));

		Assert.Equal("if (a) {\n    b();\n}", buffer.GetText());

		buffer.Undo();
		view.ExternalEdit(0);

		Assert.Equal(before, buffer.GetText());
		Assert.False(buffer.CanUndo);
	}

	[Fact]
	public void FormatAfterTypingUndoesOnlyTheFormat() {
		(TextBuffer buffer, TextView view) = Open("x", 1);

		buffer.Insert(1, "=1;");
		view.ExternalEdit(1);
		view.MoveTo(4, extend: false);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 2, 1, 3, " = "));

		Assert.Equal("x = 1;", buffer.GetText());

		buffer.Undo();

		Assert.Equal("x=1;", buffer.GetText());

		buffer.Undo();

		Assert.Equal("x", buffer.GetText());
	}

	[Fact]
	public void CaretInsideDeletedRangeLandsAtItsEnd() {
		(TextBuffer buffer, TextView view) = Open("a     b", 3);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 2, 1, 7, " "));

		Assert.Equal("a b", buffer.GetText());
		Assert.Equal(2, view.CaretOffset);
	}

	[Fact]
	public void CaretAtEndOfDocumentStaysAtEnd() {
		(TextBuffer buffer, TextView view) = Open("a;\n\n\n", 5);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 3, 4, 1, "\n"));

		Assert.Equal("a;\n", buffer.GetText());
		Assert.Equal(3, view.CaretOffset);
	}

	[Fact]
	public void NoOpFormatLeavesBufferAndUndoUntouched() {
		(TextBuffer buffer, TextView view) = Open("ok", 1);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 1, 1, 3, "ok"));

		Assert.False(buffer.CanUndo);
		Assert.Equal(1, view.CaretOffset);
	}

	[Fact]
	public void CrLfDocumentFormatKeepsLineEndings() {
		(TextBuffer buffer, TextView view) = Open("a{\r\nb;\r\n}", 4);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 2, 1, 2, " "), new HostTextEdit(2, 1, 2, 1, "    "));

		Assert.Equal("a {\r\n    b;\r\n}", buffer.GetText());
		Assert.Equal(9, view.CaretOffset);
	}

	[Fact]
	public void BackwardSelectionKeepsDirection() {
		(TextBuffer buffer, TextView view) = Open("abc def", 4, 7);

		ApplyAsFormat(buffer, view, new HostTextEdit(1, 1, 1, 1, "  "));

		Assert.Equal(9, view.SelectionAnchor);
		Assert.Equal(6, view.CaretOffset);
	}

	[Fact]
	public void ExternalEditClampsCaretWhenDocumentShrinks() {
		(TextBuffer buffer, TextView view) = Open("0123456789", 10);

		ApplyPlain(buffer, view, new HostTextEdit(1, 1, 1, 11, "x"));

		Assert.Equal(1, view.CaretOffset);
		Assert.True(view.CaretOffset <= view.Snapshot.Length);
	}

	[Fact]
	public void SnapshotMatchesBufferAfterBatch() {
		(TextBuffer buffer, TextView view) = Open("a\nb\nc", 0);

		ApplyPlain(buffer, view, new HostTextEdit(3, 1, 3, 2, "C"), new HostTextEdit(1, 1, 1, 2, "A"));

		Assert.Equal(buffer.GetText(), view.Snapshot.GetText());
	}

	[Theory]
	[InlineData(11)]
	[InlineData(12)]
	[InlineData(13)]
	[InlineData(14)]
	public void RandomFormatsKeepCaretOnTheSameCharacter(int seed) {
		Random random = new(seed);

		for (int round = 0; round < 200; round++) {
			string[] words = [.. Enumerable.Range(0, random.Next(2, 12)).Select(static i => "w" + (char)('a' + i))];
			string separator = random.Next(2) == 0 ? " " : "\n";
			string before = string.Join(separator, words);
			string after = string.Join("", words.Select((word, i) => (i == 0 ? "" : random.Next(3) switch { 0 => " ", 1 => "\n    ", _ => "  " }) + word));
			int target = random.Next(words.Length);
			int caret = (target * (2 + separator.Length)) + 1;
			(TextBuffer buffer, TextView view) = Open(before, caret);
			int lines = before.Count(static c => c == '\n');
			int lastLength = before.Length - (before.LastIndexOf('\n') + 1);

			ApplyAsFormat(buffer, view, new HostTextEdit(1, 1, lines + 1, lastLength + 1, after));

			Assert.Equal(after, buffer.GetText());
			Assert.Equal(words[target][1..], buffer.GetText()[view.CaretOffset..][..(words[target].Length - 1)]);
		}
	}
}
