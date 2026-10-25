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
}
