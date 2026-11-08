using Forge.Editor;
using Forge.Text;

namespace Forge.Tests;

public class FindModelTests
{
	private static FindModel Find(string text, string query, FindOptions options, (int, int)? scope = null) {
		FindModel model = new();
		model.Update(new TextBuffer(text).CreateSnapshot(), query, options, scope);
		return model;
	}

	[Fact]
	public void WholeWordSkipsMatchesInsideWords() {
		FindModel model = Find("cat concat cat_ cat.", "cat", new FindOptions(false, true, false));

		Assert.Equal([0, 16], model.Matches.Select(static match => match.Start));
	}

	[Fact]
	public void MatchCaseIsRespected() {
		Assert.Equal(2, Find("Foo foo", "foo", new FindOptions(false, false, false)).Matches.Count);
		Assert.Single(Find("Foo foo", "foo", new FindOptions(true, false, false)).Matches);
	}

	[Fact]
	public void RegexReplacementExpandsGroups() {
		const string text = "a1 b2";
		FindModel model = Find(text, @"(\w)(\d)", new FindOptions(false, false, true));

		Assert.Equal(2, model.Matches.Count);
		Assert.Equal("1a", model.ReplacementFor(text, model.Matches[0], "$2$1", false));
	}

	[Fact]
	public void InvalidRegexReportsNoMatches() {
		FindModel model = Find("abc", "(", new FindOptions(false, false, true));

		Assert.True(model.InvalidPattern);
		Assert.Empty(model.Matches);
	}

	[Fact]
	public void ScopeLimitsMatches() {
		Assert.Single(Find("x x x", "x", new FindOptions(false, false, false), (1, 4)).Matches);
	}

	[Theory]
	[InlineData("FOO", "bar", "BAR")]
	[InlineData("foo", "Bar", "bar")]
	[InlineData("Foo", "bar", "Bar")]
	public void PreserveCaseFollowsTheMatch(string matched, string replacement, string expected) => Assert.Equal(expected, FindModel.PreserveCase(matched, replacement));

	[Fact]
	public void NextWrapsAround() {
		FindModel model = Find("a a", "a", new FindOptions(false, false, false));

		Assert.Equal(0, model.Next(3));
		Assert.Equal(1, model.Previous(0));
	}
}
