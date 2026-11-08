using System.Collections.Concurrent;

using Forge.Search;

namespace Forge.Tests;

public sealed class TextSearchEngineTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-search-" + Guid.NewGuid().ToString("N"));

	public TextSearchEngineTests() {
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		Directory.CreateDirectory(Path.Combine(_root, "node_modules", "lib"));
		File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "int count = 1;\nCount++;\nrecount();\n");
		File.WriteAllText(Path.Combine(_root, "src", "b.txt"), "nothing here\n");
		File.WriteAllText(Path.Combine(_root, "node_modules", "lib", "c.cs"), "count\n");
		File.WriteAllBytes(Path.Combine(_root, "src", "bin.dat"), [99, 111, 117, 110, 116, 0, 1, 2]);
	}

	public void Dispose() {
		try {
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException) {
		}
	}

	private List<FileSearchResult> Search(string pattern, bool matchCase = false, bool wholeWord = false, bool regex = false, string include = "", string exclude = "", bool useExclude = true) {
		TextSearchQuery query = new(pattern, matchCase, wholeWord, regex, include, exclude, useExclude);
		ConcurrentBag<FileSearchResult> results = [];
		TextSearchEngine.Run(_root, query, TextSearchEngine.Compile(query, out _)!, results.Add, CancellationToken.None);
		return [.. results.OrderBy(result => result.RelativePath, StringComparer.Ordinal)];
	}

	[Fact]
	public void FindsCaseInsensitiveLiteralMatchesWithPositions() {
		FileSearchResult result = Assert.Single(Search("count"));

		Assert.Equal("src/a.cs", result.RelativePath.Replace('\\', '/'));
		Assert.Equal([new TextMatch(0, 4, 5), new TextMatch(1, 0, 5), new TextMatch(2, 2, 5)], result.Matches);
		Assert.Equal("Count++;", result.Lines[1]);
	}

	[Fact]
	public void MatchCaseRestrictsResults() => Assert.Equal(2, Assert.Single(Search("count", matchCase: true)).Matches.Length);

	[Fact]
	public void WholeWordSkipsPartialWords() => Assert.Equal(2, Assert.Single(Search("count", wholeWord: true)).Matches.Length);

	[Fact]
	public void RegexMatches() => Assert.Single(Assert.Single(Search(@"re\w+\(\)", regex: true)).Matches);

	[Fact]
	public void DisablingExcludesSearchesNodeModules() => Assert.Equal(2, Search("count", useExclude: false).Count);

	[Fact]
	public void IncludeFiltersByGlob() => Assert.Empty(Search("count", include: "*.txt"));

	[Fact]
	public void ExcludeFiltersByFolderName() => Assert.Empty(Search("count", exclude: "src"));

	[Fact]
	public void InvalidRegexReportsAnError() {
		Assert.Null(TextSearchEngine.Compile(new TextSearchQuery("(", false, false, true, "", "", true), out string? error));
		Assert.NotNull(error);
	}

	[Fact]
	public void GlobsMatchNestedPaths() {
		GlobSet set = GlobSet.Parse("*.cs, docs/**");

		Assert.True(set.Matches("src/deep/file.cs"));
		Assert.True(set.Matches("docs/a/b.md"));
		Assert.False(set.Matches("src/file.ts"));
	}
}
