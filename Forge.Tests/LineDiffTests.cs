using Forge.Text;

namespace Forge.Tests;

public class LineDiffTests
{
	[Fact]
	public void IdenticalTextHasNoHunks() => Assert.Empty(LineDiff.Compute("a\nb\nc\n", "a\nb\nc\n"));

	[Fact]
	public void DetectsAnInsertedLine() {
		LineHunk hunk = Assert.Single(LineDiff.Compute("a\nb\nc\n", "a\nb\nx\nc\n"));

		Assert.Equal(new LineHunk(2, 0, 2, 1), hunk);
	}

	[Fact]
	public void DetectsADeletedLine() {
		LineHunk hunk = Assert.Single(LineDiff.Compute("a\nb\nc\n", "a\nc\n"));

		Assert.Equal(new LineHunk(1, 1, 1, 0), hunk);
	}

	[Fact]
	public void DetectsAModifiedLine() {
		LineHunk hunk = Assert.Single(LineDiff.Compute("a\nb\nc\n", "a\nB\nc\n"));

		Assert.Equal(new LineHunk(1, 1, 1, 1), hunk);
	}

	[Fact]
	public void SeparatesDistantChanges() {
		List<LineHunk> hunks = LineDiff.Compute("1\n2\n3\n4\n5\n6\n", "1\nX\n3\n4\n5\nY\n6\n");

		Assert.Equal([new LineHunk(1, 1, 1, 1), new LineHunk(5, 0, 5, 1)], hunks);
	}

	[Fact]
	public void EmptyOriginalIsOneInsertion() => Assert.Equal([new LineHunk(0, 0, 0, 2)], LineDiff.Compute(string.Empty, "a\nb\n"));

	[Fact]
	public void EmptyModifiedIsOneDeletion() => Assert.Equal([new LineHunk(0, 2, 0, 0)], LineDiff.Compute("a\nb\n", string.Empty));

	[Fact]
	public void IgnoresLineEndingDifferences() => Assert.Empty(LineDiff.Compute("a\r\nb\r\n", "a\nb\n"));

	[Fact]
	public void ApplyingHunksReproducesTheModifiedText() {
		Random random = new(7);

		for (int round = 0; round < 200; round++) {
			string[] original = [.. Enumerable.Range(0, random.Next(0, 30)).Select(_ => ((char)('a' + random.Next(5))).ToString())];
			List<string> modifiedLines = [.. original];

			for (int edit = random.Next(0, 6); edit > 0; edit--) {
				int at = random.Next(0, modifiedLines.Count + 1);

				if (random.Next(2) == 0 || modifiedLines.Count == 0 || at == modifiedLines.Count) modifiedLines.Insert(at, ((char)('a' + random.Next(5))).ToString());
				else modifiedLines.RemoveAt(at);
			}

			string left = string.Concat(original.Select(line => line + "\n"));
			string right = string.Concat(modifiedLines.Select(line => line + "\n"));
			List<LineHunk> hunks = LineDiff.Compute(left, right);
			List<string> rebuilt = [.. original];

			for (int i = hunks.Count - 1; i >= 0; i--) {
				LineHunk hunk = hunks[i];
				rebuilt.RemoveRange(hunk.OriginalStart, hunk.OriginalCount);
				rebuilt.InsertRange(hunk.OriginalStart, modifiedLines.GetRange(hunk.ModifiedStart, hunk.ModifiedCount));
			}

			Assert.Equal(modifiedLines, rebuilt);
		}
	}
}
