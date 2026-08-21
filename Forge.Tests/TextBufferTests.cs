using Forge.Text;

namespace Forge.Tests;

public class TextBufferTests
{
	[Fact]
	public void EmptyBufferHasOneLine() {
		TextBuffer buffer = new();

		Assert.Equal(0, buffer.Length);
		Assert.Equal(1, buffer.LineCount);
		Assert.Equal(string.Empty, buffer.GetText());
	}

	[Fact]
	public void InsertIntoEmptyBuffer() {
		TextBuffer buffer = new();
		buffer.Insert(0, "hello");

		Assert.Equal("hello", buffer.GetText());
		Assert.Equal(5, buffer.Length);
		buffer.Validate();
	}

	[Theory]
	[InlineData(0, "XYhello world")]
	[InlineData(5, "helloXY world")]
	[InlineData(11, "hello worldXY")]
	public void InsertAtOffset(int offset, string expected) {
		TextBuffer buffer = new("hello world");
		buffer.Insert(offset, "XY");

		Assert.Equal(expected, buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void InsertBeyondEndClampsToEnd() {
		TextBuffer buffer = new("abc");
		buffer.Insert(9999, "!");

		Assert.Equal("abc!", buffer.GetText());
		buffer.Validate();
	}

	[Theory]
	[InlineData(0, 6, "world")]
	[InlineData(5, 6, "hello")]
	[InlineData(3, 3, "helworld")]
	public void Delete(int offset, int count, string expected) {
		TextBuffer buffer = new("hello world");
		buffer.Delete(offset, count);

		Assert.Equal(expected, buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void DeleteEverything() {
		TextBuffer buffer = new("hello world");
		buffer.Delete(0, buffer.Length);

		Assert.Equal(string.Empty, buffer.GetText());
		Assert.Equal(0, buffer.Length);
		Assert.Equal(1, buffer.LineCount);
		buffer.Validate();
	}

	[Fact]
	public void DeletePastEndIsClamped() {
		TextBuffer buffer = new("abc");
		buffer.Delete(1, 100);

		Assert.Equal("a", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void DeleteSpanningManyPieces() {
		TextBuffer buffer = new();
		for (int i = 0; i < 20; i++) buffer.Insert(0, i.ToString("D2"));

		string expected = buffer.GetText();
		buffer.Delete(5, 30);

		Assert.Equal(string.Concat(expected[..5], expected[35..]), buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void Replace() {
		TextBuffer buffer = new("hello world");
		buffer.Replace(6, 5, "forge");

		Assert.Equal("hello forge", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void SplitInsertProducesThreePieces() {
		TextBuffer buffer = new("abcdef");
		buffer.Insert(3, "--");

		Assert.Equal("abc--def", buffer.GetText());
		Assert.Equal('-', buffer[3]);
		Assert.Equal('d', buffer[5]);
		buffer.Validate();
	}

	[Fact]
	public void SequentialTypingIsContiguous() {
		TextBuffer buffer = new();
		for (int i = 0; i < 500; i++) buffer.Insert(buffer.Length, ((char)('a' + (i % 26))).ToString());

		Assert.Equal(500, buffer.Length);
		for (int i = 0; i < 500; i++) Assert.Equal((char)('a' + (i % 26)), buffer[i]);

		buffer.Validate();
	}

	[Fact]
	public void GetTextRangeReadsAcrossPieces() {
		TextBuffer buffer = new("hello world");
		buffer.Insert(5, ", cruel");

		Assert.Equal("hello, cruel world", buffer.GetText());
		Assert.Equal("o, cruel w", buffer.GetText(4, 10));
		buffer.Validate();
	}
}
