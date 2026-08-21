using System.Text;

using Forge.Text;

namespace Forge.Tests;

public class TextLoaderTests
{
	[Fact]
	public void LoadsPlainAscii() {
		TextBuffer buffer = LoadFrom("hello\nworld"u8.ToArray());

		Assert.Equal("hello\nworld", buffer.GetText());
		Assert.Equal(2, buffer.LineCount);
	}

	[Fact]
	public void SkipsUtf8Bom() {
		byte[] bytes = [0xEF, 0xBB, 0xBF, .. "hello"u8];
		TextBuffer buffer = LoadFrom(bytes);

		Assert.Equal("hello", buffer.GetText());
	}

	[Fact]
	public void ReadsUtf16LittleEndian() {
		byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("héllo\nwörld")];
		TextBuffer buffer = LoadFrom(bytes);

		Assert.Equal("héllo\nwörld", buffer.GetText());
	}

	[Fact]
	public void ReadsUtf16BigEndian() {
		byte[] bytes = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("héllo")];
		TextBuffer buffer = LoadFrom(bytes);

		Assert.Equal("héllo", buffer.GetText());
	}

	[Fact]
	public void DecodesMultiByteRunsSplitAcrossReadBoundaries() {
		string expected = string.Concat(Enumerable.Repeat("日本語テキスト", 40_000));
		TextBuffer buffer = LoadFrom(Encoding.UTF8.GetBytes(expected));

		Assert.Equal(expected.Length, buffer.Length);
		Assert.Equal(expected, buffer.GetText());
	}

	[Theory]
	[InlineData("a\r\nb", LineEnding.CrLf)]
	[InlineData("a\nb", LineEnding.Lf)]
	[InlineData("no terminators", LineEnding.CrLf)]
	public void DetectsLineEnding(string text, LineEnding expected) {
		TextBuffer buffer = LoadFrom(Encoding.UTF8.GetBytes(text));

		Assert.Equal(expected, buffer.LineEnding);
	}

	[Fact]
	public void LoadsEmptyFile() {
		TextBuffer buffer = LoadFrom([]);

		Assert.Equal(0, buffer.Length);
		Assert.Equal(1, buffer.LineCount);
	}

	[Fact]
	public void LoadedBufferIsImmediatelyEditable() {
		TextBuffer buffer = LoadFrom("hello world"u8.ToArray());
		buffer.Insert(5, ",");

		Assert.Equal("hello, world", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void ChunkedDocumentReadsBackAsOne() {
		char[][] chunks = ["hello, ".ToCharArray(), "world\nsecond".ToCharArray()];
		TextBuffer buffer = new(chunks, [7, 12], LineEnding.Lf);

		Assert.Equal("hello, world\nsecond", buffer.GetText());
		Assert.Equal(2, buffer.LineCount);
		buffer.Validate();
	}

	[Fact]
	public void ChunkedDocumentIsEditableAcrossTheSeam() {
		char[][] chunks = ["abc".ToCharArray(), "def".ToCharArray(), "ghi".ToCharArray()];
		TextBuffer buffer = new(chunks, [3, 3, 3], LineEnding.Lf);

		buffer.Insert(3, "-");
		buffer.Delete(7, 1);

		Assert.Equal("abc-defhi", buffer.GetText());
		buffer.Validate();
	}

	[Fact]
	public void LoadsAcrossManyReads() {
		string line = new('x', 127);
		System.Text.StringBuilder text = new();

		for (int i = 0; i < 4096; i++) text.Append(line).Append('\n');

		string expected = text.ToString();
		TextBuffer buffer = LoadFrom(System.Text.Encoding.UTF8.GetBytes(expected));

		Assert.Equal(expected.Length, buffer.Length);
		Assert.Equal(expected, buffer.GetText());
		buffer.Validate();
	}

	private static TextBuffer LoadFrom(byte[] bytes) {
		using MemoryStream stream = new(bytes);
		return TextLoader.Load(stream, bytes.Length);
	}
}
