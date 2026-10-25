using System.Buffers;
using System.Text;

namespace Forge.Text;

public static class TextLoader
{
	private const int ReadBufferSize = 128 * 1024;

	private const int ChunkChars = 16 * 1024 * 1024;

	private const int TrimThreshold = 64 * 1024;

	public static TextBuffer Load(string path) {
		using FileStream stream = new(
			path,
			new FileStreamOptions {
				Mode = FileMode.Open,
				Access = FileAccess.Read,
				Share = FileShare.ReadWrite | FileShare.Delete,
				Options = FileOptions.SequentialScan,
				BufferSize = 0,
			});

		return Load(stream, stream.Length);
	}

	public static TextBuffer Load(Stream stream, long expectedByteLength = -1) {
		byte[] scratch = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
		try {
			int prefixLength = FillPrefix(stream, scratch);
			Encoding encoding = DetectEncoding(scratch.AsSpan(0, prefixLength), out int bomLength);

			Decoder decoder = encoding.GetDecoder();

			int bytesPerChar = encoding.CodePage is 1200 or 1201 ? 2 : 1;

			long estimate = expectedByteLength > 0 ? (expectedByteLength / bytesPerChar) + 16 : 4096;

			int capacity = (int)Math.Min(estimate, ChunkChars);

			List<char[]> chunks = [];
			List<int> lengths = [];

			char[] chars = new char[Math.Max(capacity, 16)];
			int charCount = 0;

			long consumed = bomLength;

			int pending = prefixLength - bomLength;
			if (pending > 0) scratch.AsSpan(bomLength, pending).CopyTo(scratch);

			while (true) {
				if (pending > 0) {
					if (chars.Length - charCount < pending + 1) {
						chunks.Add(chars);
						lengths.Add(charCount);

						chars = new char[NextCapacity(expectedByteLength, consumed, bytesPerChar)];
						charCount = 0;
					}

					charCount += decoder.GetChars(scratch, 0, pending, chars, charCount, flush: false);
					consumed += pending;
				}

				pending = stream.Read(scratch, 0, ReadBufferSize);
				if (pending <= 0) break;
			}

			if (chars.Length - charCount < 8) {
				chunks.Add(chars);
				lengths.Add(charCount);

				chars = new char[16];
				charCount = 0;
			}

			charCount += decoder.GetChars([], 0, 0, chars, charCount, flush: true);

			if (charCount > 0 && chars.Length - charCount > TrimThreshold) {
				char[] trimmed = new char[charCount];
				chars.AsSpan(0, charCount).CopyTo(trimmed);
				chars = trimmed;
			}

			chunks.Add(chars);
			lengths.Add(charCount);

			LineEnding lineEnding = DetectLineEnding(chunks[0].AsSpan(0, lengths[0]));
			return new TextBuffer([.. chunks], [.. lengths], lineEnding) { Encoding = encoding };
		}
		finally {
			ArrayPool<byte>.Shared.Return(scratch);
		}
	}

	private static int NextCapacity(long expectedByteLength, long consumed, int bytesPerChar) {
		if (expectedByteLength <= 0) return ChunkChars;

		long remaining = (expectedByteLength - consumed) / bytesPerChar;

		return (int)Math.Clamp(remaining + ReadBufferSize + 1, ReadBufferSize * 2, ChunkChars);
	}

	private static int FillPrefix(Stream stream, byte[] scratch) {
		int total = 0;
		while (total < ReadBufferSize) {
			int read = stream.Read(scratch, total, ReadBufferSize - total);
			if (read <= 0) break;

			total += read;
		}

		return total;
	}

	private static Encoding DetectEncoding(ReadOnlySpan<byte> prefix, out int bomLength) {
		if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) {
			bomLength = 3;
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
		}

		if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE) {
			bomLength = 2;
			return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
		}

		if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF) {
			bomLength = 2;
			return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
		}

		bomLength = 0;
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
	}

	private static LineEnding DetectLineEnding(ReadOnlySpan<char> text) {
		int lineFeed = text.IndexOf('\n');
		if (lineFeed < 0) return LineEnding.CrLf;

		return lineFeed > 0 && text[lineFeed - 1] == '\r' ? LineEnding.CrLf : LineEnding.Lf;
	}
}
