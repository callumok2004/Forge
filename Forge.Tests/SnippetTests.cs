using Forge.Language;

namespace Forge.Tests;

public class SnippetTests
{
	[Fact]
	public void FinalStopBecomesCaret() {
		(string text, int caret) = Snippet.Expand("/// <summary>\n/// $0\n/// </summary>");

		Assert.Equal("/// <summary>\n/// \n/// </summary>", text);
		Assert.Equal("/// <summary>\n/// ".Length, caret);
	}

	[Fact]
	public void PlaceholdersKeepTextAndFirstStopWins() {
		(string text, int caret) = Snippet.Expand("${1:name} = ${2:value};$0");

		Assert.Equal("name = value;", text);
		Assert.Equal(0, caret);
	}

	[Fact]
	public void EscapedDollarIsLiteral() => Assert.Equal("$x", Snippet.Expand("\\$x").Text);
}
