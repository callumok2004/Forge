using Forge.Text;

namespace Forge.Tests;

public class LineIndexTests
{
	[Fact]
	public void LineCountCountsTerminatorsNotLines() {
		Assert.Equal(1, new TextBuffer("").LineCount);
		Assert.Equal(1, new TextBuffer("one").LineCount);
		Assert.Equal(2, new TextBuffer("one\n").LineCount);
		Assert.Equal(2, new TextBuffer("one\ntwo").LineCount);
		Assert.Equal(3, new TextBuffer("one\ntwo\n").LineCount);
	}

	[Fact]
	public void LineStartOffsets() {
		TextBuffer buffer = new("alpha\nbeta\ngamma");

		Assert.Equal(0, buffer.GetLineStartOffset(0));
		Assert.Equal(6, buffer.GetLineStartOffset(1));
		Assert.Equal(11, buffer.GetLineStartOffset(2));
	}

	[Fact]
	public void LineStartOffsetsWithCrLf() {
		TextBuffer buffer = new("alpha\r\nbeta\r\ngamma");

		Assert.Equal(0, buffer.GetLineStartOffset(0));
		Assert.Equal(7, buffer.GetLineStartOffset(1));
		Assert.Equal(13, buffer.GetLineStartOffset(2));
	}

	[Fact]
	public void LineLengthExcludesTerminator() {
		TextBuffer buffer = new("alpha\nbeta\ngamma");

		Assert.Equal(5, buffer.GetLineLength(0));
		Assert.Equal(4, buffer.GetLineLength(1));
		Assert.Equal(5, buffer.GetLineLength(2));
	}

	[Fact]
	public void LineLengthExcludesCarriageReturn() {
		TextBuffer buffer = new("alpha\r\nbeta\r\ngamma");

		Assert.Equal(5, buffer.GetLineLength(0));
		Assert.Equal(4, buffer.GetLineLength(1));
		Assert.Equal(5, buffer.GetLineLength(2));
	}

	[Fact]
	public void LineForOffset() {
		TextBuffer buffer = new("alpha\nbeta\ngamma");

		Assert.Equal(0, buffer.GetLineAt(0));
		Assert.Equal(0, buffer.GetLineAt(5));
		Assert.Equal(1, buffer.GetLineAt(6));
		Assert.Equal(1, buffer.GetLineAt(10));
		Assert.Equal(2, buffer.GetLineAt(11));
		Assert.Equal(2, buffer.GetLineAt(16));
	}

	[Fact]
	public void PositionRoundTrips() {
		TextBuffer buffer = new("alpha\nbeta\ngamma");

		for (int offset = 0; offset <= buffer.Length; offset++) {
			TextPosition position = buffer.GetPosition(offset);
			Assert.Equal(offset, buffer.GetOffset(position));
		}
	}

	[Fact]
	public void LineIndexSurvivesEdits() {
		TextBuffer buffer = new("alpha\nbeta\ngamma");
		buffer.Insert(6, "NEW\n");

		Assert.Equal("alpha\nNEW\nbeta\ngamma", buffer.GetText());
		Assert.Equal(4, buffer.LineCount);
		Assert.Equal(6, buffer.GetLineStartOffset(1));
		Assert.Equal(10, buffer.GetLineStartOffset(2));
		Assert.Equal(15, buffer.GetLineStartOffset(3));
		buffer.Validate();
	}

	[Fact]
	public void DeletingANewLineJoinsLines() {
		TextBuffer buffer = new("alpha\nbeta");
		buffer.Delete(5, 1);

		Assert.Equal("alphabeta", buffer.GetText());
		Assert.Equal(1, buffer.LineCount);
		buffer.Validate();
	}

	[Fact]
	public void LineLookupsAgreeWithNaiveScanAcrossManyPieces() {
		TextBuffer buffer = new();
		for (int i = 0; i < 200; i++) buffer.Insert(buffer.Length / 2, $"line{i}\n");

		string text = buffer.GetText();
		string[] expected = text.Split('\n');

		Assert.Equal(expected.Length, buffer.LineCount);

		int offset = 0;
		for (int line = 0; line < expected.Length; line++) {
			Assert.Equal(offset, buffer.GetLineStartOffset(line));
			Assert.Equal(expected[line].Length, buffer.GetLineLength(line));
			offset += expected[line].Length + 1;
		}

		buffer.Validate();
	}
}
