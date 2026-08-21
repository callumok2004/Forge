using System.Text;

using Forge.Text;

namespace Forge.Tests;

public class PieceTreeFuzzTests
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(12345)]
	[InlineData(987654321)]
	public void RandomEditsMatchOracle(int seed) {
		Random random = new(seed);
		TextBuffer buffer = new("The quick brown fox\njumps over\r\nthe lazy dog\n");
		StringBuilder oracle = new(buffer.GetText());

		for (int step = 0; step < 400; step++) {
			if (oracle.Length > 0 && random.Next(100) < 40) {
				int offset = random.Next(oracle.Length);
				int count = random.Next(1, Math.Min(24, oracle.Length - offset) + 1);

				buffer.Delete(offset, count);
				oracle.Remove(offset, count);
			}
			else {
				int offset = random.Next(oracle.Length + 1);
				string text = RandomText(random);

				buffer.Insert(offset, text);
				oracle.Insert(offset, text);
			}

			buffer.Validate();

			Assert.Equal(oracle.Length, buffer.Length);
			Assert.Equal(oracle.ToString(), buffer.GetText());
		}

		AssertLineIndexMatches(buffer, oracle.ToString());
	}

	[Theory]
	[InlineData(7)]
	[InlineData(42)]
	[InlineData(2024)]
	public void RandomEditsKeepLineIndexCorrect(int seed) {
		Random random = new(seed);
		TextBuffer buffer = new();
		StringBuilder oracle = new();

		for (int step = 0; step < 300; step++) {
			if (oracle.Length > 0 && random.Next(100) < 35) {
				int offset = random.Next(oracle.Length);
				int count = random.Next(1, Math.Min(16, oracle.Length - offset) + 1);

				buffer.Delete(offset, count);
				oracle.Remove(offset, count);
			}
			else {
				int offset = random.Next(oracle.Length + 1);

				string text = random.Next(100) switch {
					< 25 => "\n",
					< 40 => "\r\n",
					_ => RandomText(random),
				};

				buffer.Insert(offset, text);
				oracle.Insert(offset, text);
			}

			buffer.Validate();
			AssertLineIndexMatches(buffer, oracle.ToString());
		}
	}

	[Theory]
	[InlineData(11)]
	[InlineData(99)]
	public void SnapshotsAreUnaffectedByLaterEdits(int seed) {
		Random random = new(seed);
		TextBuffer buffer = new("initial contents\nsecond line\n");

		List<(TextSnapshot Snapshot, string Expected)> taken = [];

		for (int step = 0; step < 60; step++) {
			TextSnapshot snapshot = buffer.CreateSnapshot();
			taken.Add((snapshot, buffer.GetText()));

			int offset = random.Next(buffer.Length + 1);
			buffer.Insert(offset, RandomText(random));

			if (buffer.Length > 20 && random.Next(2) == 0) {
				int deleteAt = random.Next(buffer.Length - 10);
				buffer.Delete(deleteAt, random.Next(1, 10));
			}
		}

		foreach ((TextSnapshot snapshot, string expected) in taken) {
			Assert.Equal(expected, snapshot.GetText());
			Assert.Equal(expected.Length, snapshot.Length);
			AssertSnapshotLineIndexMatches(snapshot, expected);
		}
	}

	[Fact]
	public void SnapshotIsCachedPerVersion() {
		TextBuffer buffer = new("abc");

		TextSnapshot first = buffer.CreateSnapshot();
		Assert.Same(first, buffer.CreateSnapshot());

		buffer.Insert(0, "x");
		TextSnapshot second = buffer.CreateSnapshot();

		Assert.NotSame(first, second);
		Assert.Equal("abc", first.GetText());
		Assert.Equal("xabc", second.GetText());
	}

	private static string RandomText(Random random) {
		int length = random.Next(1, 12);
		return string.Create(length, random, static (span, rng) => {
			for (int i = 0; i < span.Length; i++) span[i] = rng.Next(10) == 0 ? '\n' : (char)('a' + rng.Next(26));
		});
	}

	private static void AssertLineIndexMatches(TextBuffer buffer, string expected) {
		string[] lines = expected.Split('\n');
		Assert.Equal(lines.Length, buffer.LineCount);

		int offset = 0;
		for (int line = 0; line < lines.Length; line++) {
			Assert.Equal(offset, buffer.GetLineStartOffset(line));

			int expectedLength = lines[line].EndsWith('\r') ? lines[line].Length - 1 : lines[line].Length;
			Assert.Equal(expectedLength, buffer.GetLineLength(line));
			Assert.Equal(line, buffer.GetLineAt(offset));

			offset += lines[line].Length + 1;
		}
	}

	private static void AssertSnapshotLineIndexMatches(TextSnapshot snapshot, string expected) {
		string[] lines = expected.Split('\n');
		Assert.Equal(lines.Length, snapshot.LineCount);

		int offset = 0;
		for (int line = 0; line < lines.Length; line++) {
			Assert.Equal(offset, snapshot.GetLineStartOffset(line));

			int expectedLength = lines[line].EndsWith('\r') ? lines[line].Length - 1 : lines[line].Length;
			Assert.Equal(expectedLength, snapshot.GetLineLength(line));

			offset += lines[line].Length + 1;
		}
	}
}
