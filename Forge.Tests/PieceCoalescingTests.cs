using Forge.Text;

namespace Forge.Tests;

public class PieceCoalescingTests
{
	[Fact]
	public void InsertAndDeleteRoundTripLeavesTheTextAlone() {
		TextBuffer buffer = new("hello world");

		for (int i = 0; i < 200; i++) {
			buffer.Insert(5, "XYZ");
			buffer.Delete(5, 3);
		}

		Assert.Equal("hello world", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void DeletingAWholePieceLeavesItsNeighboursIntact() {
		TextBuffer buffer = new("abcdef");

		buffer.Insert(3, "--");
		Assert.Equal("abc--def", buffer.GetText());

		buffer.Delete(3, 2);

		Assert.Equal("abcdef", buffer.GetText());
		Assert.Equal(1, buffer.LineCount);
		buffer.Validate();
	}

	[Fact]
	public void DeletingAcrossPiecesKeepsTheSurvivingHalves() {
		TextBuffer buffer = new("0123456789");

		buffer.Insert(5, "ab");
		buffer.Insert(3, "cd");

		buffer.Delete(3, 2);
		buffer.Delete(5, 2);

		Assert.Equal("0123456789", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void SplittingAPieceKeepsTheLineIndexRight() {
		TextBuffer buffer = new("one\ntwo\nthree\nfour");

		buffer.Insert(4, "X\nY");

		Assert.Equal("one\nX\nYtwo\nthree\nfour", buffer.GetText());
		Assert.Equal(5, buffer.LineCount);
		Assert.Equal(4, buffer.GetLineStartOffset(1));
		Assert.Equal(6, buffer.GetLineStartOffset(2));

		buffer.Delete(4, 3);

		Assert.Equal("one\ntwo\nthree\nfour", buffer.GetText());
		Assert.Equal(4, buffer.LineCount);
		Assert.Equal(4, buffer.GetLineStartOffset(1));
		Assert.Equal(8, buffer.GetLineStartOffset(2));
		buffer.Validate();
	}

	[Fact]
	public void DeletingAHoleInTheMiddleKeepsTheLineIndexRight() {
		TextBuffer buffer = new("a\nb\nc\nd\ne");

		buffer.Delete(2, 4);

		Assert.Equal("a\nd\ne", buffer.GetText());
		Assert.Equal(3, buffer.LineCount);
		Assert.Equal(2, buffer.GetLineStartOffset(1));
		Assert.Equal(4, buffer.GetLineStartOffset(2));
		buffer.Validate();
	}

	[Fact]
	public void TypingThenDeletingInTheMiddleRestoresTheDocument() {
		TextBuffer buffer = new("public void Method()\r\n{\r\n}\r\n");

		for (int i = 0; i < 5; i++) buffer.Insert(12 + i, "abcde"[i].ToString());

		buffer.Delete(12, 5);

		Assert.Equal("public void Method()\r\n{\r\n}\r\n", buffer.GetText());
		Assert.Equal(4, buffer.LineCount);
		buffer.Validate();
	}
}
