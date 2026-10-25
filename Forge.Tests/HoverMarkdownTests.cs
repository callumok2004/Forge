using Forge.Language;

namespace Forge.Tests;

public class HoverMarkdownTests
{
	[Theory]
	[InlineData(@"System\.Collections\.Generic", "System.Collections.Generic")]
	[InlineData(@"a\-b \(c\)", "a-b (c)")]
	[InlineData(@"keep \n letters", @"keep \n letters")]
	[InlineData("`List<T>`", "List<T>")]
	[InlineData(@"`a\.b`", @"a\.b")]
	[InlineData("**bold** and *em*", "bold and em")]
	[InlineData("snake_case_name", "snake_case_name")]
	[InlineData("see [docs](https://x.y/z) now", "see docs now")]
	[InlineData("a&nbsp;b", "a b")]
	public void Unescapes(string markdown, string expected) => Assert.Equal(expected, HoverWidget.Inline(markdown));
}
