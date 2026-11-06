using Forge.Syntax;
using Forge.Syntax.TextMate;
using Forge.Text;
using Forge.Theming;

namespace Forge.Tests;

public class GrammarColorTests
{
	[Theory]
	[InlineData(".editorconfig", "[*.cs]\nindent_style = tab\n")]
	[InlineData("a.json", "{ \"a\": 1 }\n")]
	[InlineData("a.xml", "<a b=\"c\"/>\n")]
	[InlineData("a.ts", "const a: number = 1;\n")]
	[InlineData("a.ps1", "$a = 1\n")]
	public void GrammarFilesProduceColors(string path, string text) {
		Highlighter highlighter = new(Languages.ForPath(path));
		TextSnapshot snapshot = new TextBuffer(text).CreateSnapshot();

		Assert.True(highlighter.HasGrammar, path);

		List<Highlighter.ColoredToken>? tokens = highlighter.ColorLine(snapshot, 0);

		Assert.NotNull(tokens);
		Assert.Contains(tokens, token => !token.Color.Equals(Theme.EditorForeground));
	}

	private static Color ColorAt(string path, string text, int line, int column) {
		Highlighter highlighter = new(Languages.ForPath(path));
		TextSnapshot snapshot = new TextBuffer(text).CreateSnapshot();
		List<Highlighter.ColoredToken> tokens = highlighter.ColorLine(snapshot, line)!;

		foreach (Highlighter.ColoredToken token in tokens) {
			if (column >= token.Start && column < token.Start + token.Length) return token.Color;
		}

		return Theme.EditorForeground;
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	public void EditorConfigKeysAfterCommentUseKeywordColor(string newline) {
		string text = string.Join(newline, "# comment line", "root = true", "", "[*.cs]", "indent_size = 4", "");
		Color keyword = Grammars.ColorFor(["source.ini", "keyword.other.definition.ini"]);
		Color comment = Grammars.ColorFor(["source.ini", "comment.line.number-sign.ini"]);

		Assert.NotEqual(comment, keyword);
		Assert.Equal((0, comment), (0, ColorAt(".editorconfig", text, 0, 3)));
		Assert.Equal((1, keyword), (1, ColorAt(".editorconfig", text, 1, 1)));
		Assert.Equal((4, keyword), (4, ColorAt(".editorconfig", text, 4, 1)));
	}

	[Fact]
	public void XmlTagNamesUseTagColor() {
		string text = "<Project Sdk=\"Microsoft.NET.Sdk\">\n\t<PropertyGroup />\n</Project>\n";
		Color tag = Grammars.ColorFor(["text.xml", "meta.tag.xml", "entity.name.tag.localname.xml"]);

		Assert.NotEqual(Theme.EditorForeground, tag);
		Assert.Equal(tag, ColorAt("a.csproj", text, 0, 2));
		Assert.Equal(tag, ColorAt("a.csproj", text, 1, 3));
		Assert.Equal(tag, ColorAt("a.csproj", text, 2, 3));
	}
}
