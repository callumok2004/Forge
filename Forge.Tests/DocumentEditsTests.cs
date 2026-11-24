using Forge.Common.Commands;
using Forge.Text;
using Forge.Workspace;

namespace Forge.Tests;

public class DocumentEditsTests
{
	[Fact]
	public void AppliesOutOfOrderEditsAgainstOriginalPositions() {
		TextBuffer buffer = new("alpha\nbeta\ngamma");

		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 1, 1, 6, "A"), new HostTextEdit(3, 1, 3, 6, "G"), new HostTextEdit(2, 1, 2, 5, "B")], out int earliest, out int delta);

		Assert.Equal("A\nB\nG", buffer.GetText());
		Assert.Equal(0, earliest);
		Assert.Equal(-11, delta);
	}

	[Fact]
	public void MultiLineReplaceAndCrLf() {
		TextBuffer buffer = new("one\r\ntwo\r\nthree");

		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 4, 3, 1, " ")], out _, out _);

		Assert.Equal("one three", buffer.GetText());
	}

	[Fact]
	public void ClampsOutOfRangeColumnsAndSkipsBadLines() {
		TextBuffer buffer = new("abc");

		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 99, 1, 99, "!"), new HostTextEdit(7, 1, 7, 1, "x")], out _, out _);

		Assert.Equal("abc!", buffer.GetText());
	}

	[Fact]
	public void OneUndoStepForAllEdits() {
		TextBuffer buffer = new("a b c");

		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 1, 1, 2, "x"), new HostTextEdit(1, 5, 1, 6, "z")], out _, out _);
		buffer.Undo();

		Assert.Equal("a b c", buffer.GetText());
	}

	[Fact]
	public void TrackedCaretOnlyMovesForEditsBeforeIt() {
		TextBuffer buffer = new("if(x){\ny;\n}\nz");
		int[] tracked = [buffer.GetLineStartOffset(1) + 1];

		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 3, 1, 3, " "), new HostTextEdit(2, 1, 2, 1, "    "), new HostTextEdit(4, 1, 4, 1, "\n\n\n")], tracked, minimal: true, out _, out _);

		Assert.Equal("if (x){\n    y;\n}\n\n\n\nz", buffer.GetText());
		Assert.Equal(buffer.GetLineStartOffset(1) + 5, tracked[0]);
	}

	[Fact]
	public void TrackedOffsetAtInsertionMovesPastIt() {
		TextBuffer buffer = new("a\nb");
		int[] tracked = [2];

		DocumentEdits.Apply(buffer, [new HostTextEdit(2, 1, 2, 1, "\t")], tracked, minimal: true, out _, out _);

		Assert.Equal("a\n\tb", buffer.GetText());
		Assert.Equal(3, tracked[0]);
	}

	[Fact]
	public void TrackedOffsetInsideReplacedTextMovesToItsEnd() {
		TextBuffer buffer = new("one two three");
		int[] tracked = [5];

		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 5, 1, 8, "2")], tracked, minimal: true, out _, out _);

		Assert.Equal("one 2 three", buffer.GetText());
		Assert.Equal(5, tracked[0]);
	}

	[Fact]
	public void MinimalTrimsUnchangedTextAroundAnEdit() {
		TextBuffer buffer = new("    int x;\nint y;");
		int[] tracked = [8, buffer.GetLineStartOffset(1) + 4];

		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 1, 2, 7, "    int x;\n    int y;")], tracked, minimal: true, out int earliest, out int delta);

		Assert.Equal("    int x;\n    int y;", buffer.GetText());
		Assert.Equal(8, tracked[0]);
		Assert.Equal(buffer.GetLineStartOffset(1) + 8, tracked[1]);
		Assert.Equal(11, earliest);
		Assert.Equal(4, delta);
	}

	[Fact]
	public void MinimalSkipsEditsThatChangeNothing() {
		TextBuffer buffer = new("same");
		int version = buffer.Version;

		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 1, 1, 5, "same")], [], minimal: true, out int earliest, out _);

		Assert.Equal(version, buffer.Version);
		Assert.Equal(int.MaxValue, earliest);
	}

	[Fact]
	public void MultiLineFormatIsOneUndoStep() {
		TextBuffer buffer = new("class A{\nvoid M(){\nint x=1;\n}\n}");

		buffer.PushStackElement();
		DocumentEdits.Apply(buffer, [new HostTextEdit(1, 8, 1, 8, " "), new HostTextEdit(2, 1, 2, 1, "    "), new HostTextEdit(2, 9, 2, 9, " "), new HostTextEdit(3, 1, 3, 1, "        "), new HostTextEdit(3, 6, 3, 7, " = "), new HostTextEdit(4, 1, 4, 1, "    ")], [], minimal: true, out _, out _);
		buffer.PushStackElement();

		Assert.Equal("class A {\n    void M() {\n        int x = 1;\n    }\n}", buffer.GetText());

		buffer.Undo();

		Assert.Equal("class A{\nvoid M(){\nint x=1;\n}\n}", buffer.GetText());
	}

	[Fact]
	public void CrLfReplacementKeepsLineEndings() {
		TextBuffer buffer = new("a\r\n  b\r\nc");
		int[] tracked = [buffer.GetLineStartOffset(2)];

		DocumentEdits.Apply(buffer, [new HostTextEdit(2, 1, 2, 3, "    ")], tracked, minimal: true, out _, out _);

		Assert.Equal("a\r\n    b\r\nc", buffer.GetText());
		Assert.Equal(buffer.GetLineStartOffset(2), tracked[0]);
	}
}
