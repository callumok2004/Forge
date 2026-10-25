using Forge.Language;
using Forge.Search;

namespace Forge.Tests;

public class FuzzyScoreTests
{
	[Theory]
	[InlineData("tv", "TextView", new[] { 0, 4 })]
	[InlineData("gl", "GetLineSpan", new[] { 0, 3 })]
	[InlineData("abc", "abc", new[] { 0, 1, 2 })]
	public void MatchesCamelHumps(string pattern, string word, int[] expected) {
		Span<int> matches = stackalloc int[FuzzyScore.MaxMatches];

		Assert.True(FuzzyScore.TryScore(pattern, word, out _, matches, out int count));
		Assert.Equal(expected, matches[..count].ToArray());
	}

	[Theory]
	[InlineData("xyz", "TextView")]
	[InlineData("longer", "short")]
	[InlineData("", "word")]
	public void Rejects(string pattern, string word) => Assert.False(FuzzyScore.TryScore(pattern, word, out _));

	[Fact]
	public void PrefixBeatsScattered() {
		Assert.True(FuzzyScore.TryScore("con", "Console", out int prefix));
		Assert.True(FuzzyScore.TryScore("con", "ToCommonName", out int scattered));
		Assert.True(prefix > scattered);
	}

	[Fact]
	public void ScoringDoesNotAllocate() {
		Span<int> matches = stackalloc int[FuzzyScore.MaxMatches];
		FuzzyScore.TryScore("gls", "GetLineSpan", out _, matches, out _);

		long before = GC.GetAllocatedBytesForCurrentThread();

		for (int i = 0; i < 1000; i++) FuzzyScore.TryScore("gls", "GetLineSpan", out _, matches, out _);

		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
	}

	[Fact]
	public void QuickOpenRankPrefersNameMatches() {
		string[] files = ["src/other/Widget.cs", "src/TextView.cs", "docs/text-view-notes.md"];
		FuzzyMatch[] results = new FuzzyMatch[8];

		int count = FuzzyMatcher.Rank("TextView", files, results);

		Assert.True(count >= 1);
		Assert.Equal(1, results[0].Index);
	}
}
