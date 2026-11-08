using Forge.Text;

namespace Forge.Tests;

public class MoveDetectorTests
{
	private static readonly string[] Block = ["void Alpha() {", "    int value = Compute();", "    Store(value);", "}"];

	private static List<LineMove> Detect(string[] original, string[] modified) {
		List<LineHunk> hunks = LineDiff.Compute(string.Join('\n', original) + "\n", string.Join('\n', modified) + "\n");
		return MoveDetector.Compute(hunks, original, modified);
	}

	[Fact]
	public void DetectsABlockMovedDown() {
		string[] original = [.. Block, "a1", "b2", "c3", "d4", "e5"];
		string[] modified = ["a1", "b2", "c3", "d4", "e5", .. Block];

		LineMove move = Assert.Single(Detect(original, modified));

		Assert.Equal(new LineMove(0, 4, 5, 4), move);
	}

	[Fact]
	public void IgnoresShortMoves() {
		string[] original = ["x", "y", "z", "a1", "b2", "c3", "d4"];
		string[] modified = ["a1", "b2", "c3", "d4", "x", "y", "z"];

		Assert.Empty(Detect(original, modified));
	}

	[Fact]
	public void PlainEditsAreNotMoves() {
		string[] original = [.. Block, "tail line one", "tail line two"];
		string[] modified = [.. Block, "tail line uno", "tail line two"];

		Assert.Empty(Detect(original, modified));
	}
}
