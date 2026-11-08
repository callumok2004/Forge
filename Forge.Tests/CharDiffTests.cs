using Forge.Text;

namespace Forge.Tests;

public class CharDiffTests
{
	[Fact]
	public void HighlightsOnlyTheChangedWord() {
		List<CharRange> left = [];
		List<CharRange> right = [];

		CharDiff.Compute("int count = 1;", 4, "int total = 1;", 7, left, right);

		Assert.Equal([new CharRange(4, 4, 9)], left);
		Assert.Equal([new CharRange(7, 4, 9)], right);
	}

	[Fact]
	public void PureInsertionIsAnEmptyRangeOnTheOriginal() {
		List<CharRange> left = [];
		List<CharRange> right = [];

		CharDiff.Compute("ab", 0, "aXYZb", 0, left, right);

		Assert.Equal([new CharRange(0, 1, 1)], left);
		Assert.Equal([new CharRange(0, 1, 4)], right);
	}

	[Fact]
	public void SplitsMultiLineChangesPerLine() {
		List<CharRange> left = [];
		List<CharRange> right = [];

		CharDiff.Compute("a\nb", 0, "a\nxxxx\nb", 0, left, right);

		Assert.All(right, range => Assert.InRange(range.Line, 1, 2));
		Assert.NotEmpty(right);
	}
}
