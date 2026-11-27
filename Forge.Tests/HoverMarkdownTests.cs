using Forge.Common.Render;
using Forge.Common.UI;
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
	public void Unescapes(string markdown, string expected) => Assert.Equal(expected, HoverMarkdown.Plain(HoverMarkdown.Inline(markdown)));

	[Fact]
	public void LinksIconsAndStylesBecomeSpans() {
		List<InlineSpan> spans = HoverMarkdown.Inline(@"[$\(notebook\) Wiki](https://wiki.facepunch.com/gmod/net.Start) **Returns** `code`");

		Assert.Equal("https://wiki.facepunch.com/gmod/net.Start", spans[0].Href);
		Assert.Equal(" Wiki", spans.First(static span => span.Text == " Wiki").Text);
		Assert.Contains(spans, static span => span.Text == "Returns" && span.Style == InlineStyle.Bold);
		Assert.Contains(spans, static span => span.Text == "code" && span.Style == InlineStyle.Code);
	}

	[Fact]
	public void ThemeIconsResolveToCodicons() {
		List<InlineSpan> spans = HoverMarkdown.Inline("$(source-control) View Source");

		Assert.True(CodiconNames.TryGet("source-control", out Codicon expected));
		Assert.Equal(expected, spans[0].Icon);
	}

	[Fact]
	public void ImagesKeepTheirSource() {
		List<InlineSpan> spans = HoverMarkdown.Inline("![CLIENT/SERVER](file:///C:/icons/realm_cs.svg) **net.Start**");

		Assert.Equal("file:///C:/icons/realm_cs.svg", spans[0].Image);
		Assert.Equal("CLIENT/SERVER", spans[0].Text);
	}

	[Fact]
	public void BlocksSplitIntoParagraphsCodeRulesListsAndRows() {
		List<List<MarkdownBlock>> rows = HoverMarkdown.ParseRows("```glua\nnet.Start()\n```" + HoverMarkdown.RowSeparator + "one\ntwo\n\n--------\n* first\n* second\n\nline  \nbreak");

		Assert.Equal(2, rows.Count);
		Assert.Equal(BlockKind.Code, rows[0][0].Kind);
		Assert.Equal("glua", rows[0][0].Language);
		Assert.Equal(["one two"], rows[1].Where(static block => block.Kind == BlockKind.Paragraph).Take(1).Select(static block => block.PlainText));
		Assert.Contains(rows[1], static block => block.Kind == BlockKind.Rule);
		Assert.Equal(2, rows[1].Count(static block => block.Kind == BlockKind.ListItem));
		Assert.Contains(rows[1][^1].Spans, static span => span.IsBreak);
	}

	[Fact]
	public void LayoutMakesLinksClickableAndWrapsAtMaxWidth() {
		Forge.UI.Controls.TextSelection selection = new();
		HoverMarkdownLayout layout = new(new FakeFonts(), 1f, selection);
		string markdown = string.Join(" ", Enumerable.Repeat("Begins a new net message.", 30)) + "\n\n[$\\(notebook\\) Wiki](https://wiki.facepunch.com/gmod/net.Start) | [$\\(edit\\) Edit](https://wiki.facepunch.com/gmod/net.Start~edit)";
		float bottom = layout.Layout(HoverMarkdown.Parse(markdown), 8f, 500f, 4f, 0f, []);

		Assert.True(layout.Widest <= 508f);
		Assert.True(bottom > 4f + (19.5f * 5f));
		Assert.Equal(["https://wiki.facepunch.com/gmod/net.Start", "https://wiki.facepunch.com/gmod/net.Start~edit"], layout.Links.Select(static link => link.Href).Distinct());
		Assert.Equal(2, layout.Icons.Count);
		Assert.All(layout.Icons, static icon => Assert.NotNull(icon.Href));
	}
}
