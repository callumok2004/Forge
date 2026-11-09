using Forge.Syntax.TextMate;

namespace Forge.Tests;

public class TextMateTests
{
	private static List<(string Text, string Scope)> Tokenize(params string[] lines) => TokenizeWith("csharp", lines);

	private static List<(string Text, string Scope)> TokenizeWith(string id, params string[] lines) {
		GrammarTokenizer tokenizer = new(Grammars.ForId(id) ?? throw new InvalidOperationException($"no {id} grammar"));
		GrammarState state = GrammarState.Root;
		List<(string, string)> result = [];
		List<ScopedToken> tokens = [];

		foreach (string line in lines) {
			tokens.Clear();
			state = tokenizer.Tokenize(line, state, tokens);

			foreach (ScopedToken token in tokens) result.Add((line.Substring(token.Start, token.Length), token.Scopes.Count > 0 ? token.Scopes[^1] : string.Empty));
		}

		return result;
	}

	[Fact]
	public void PhpCodeIsHighlightedThroughEmbeddedGrammar() {
		List<(string Text, string Scope)> tokens = TokenizeWith("php", "<?php", "function greet($name) { return \"hi\"; }", "?>", "<div class=\"x\"><?= $name ?></div>");

		Assert.Contains(tokens, token => token.Text == "function" && token.Scope.StartsWith("storage.type.function", StringComparison.Ordinal));
		Assert.Contains(tokens, token => token.Text == "name" && token.Scope.StartsWith("variable.other.php", StringComparison.Ordinal));
		Assert.Contains(tokens, token => token.Text == "div" && token.Scope.StartsWith("entity.name.tag", StringComparison.Ordinal));
		Assert.Contains(tokens, token => token.Text == "<?=" && token.Scope.StartsWith("punctuation.section.embedded.begin.php", StringComparison.Ordinal));
	}

	[Fact]
	public void HtmlTagsAreHighlighted() {
		List<(string Text, string Scope)> tokens = TokenizeWith("html", "<p id=\"a\">text</p>");

		Assert.Contains(tokens, token => token.Text == "p" && token.Scope.StartsWith("entity.name.tag", StringComparison.Ordinal));
		Assert.Contains(tokens, token => token.Text == "id" && token.Scope.StartsWith("entity.other.attribute-name", StringComparison.Ordinal));
	}

	[Fact]
	public void KeywordsAndNames() {
		List<(string Text, string Scope)> tokens = Tokenize("public sealed class Foo : Bar");

		Assert.Contains(tokens, token => token.Text == "public" && token.Scope.StartsWith("storage.modifier", StringComparison.Ordinal));
		Assert.Contains(tokens, token => token.Text == "class" && token.Scope.StartsWith("storage.type.class", StringComparison.Ordinal));
		Assert.Contains(tokens, token => token.Text == "Foo" && token.Scope.StartsWith("entity.name.type.class", StringComparison.Ordinal));
	}

	[Fact]
	public void StringsAndEscapes() {
		List<(string Text, string Scope)> tokens = Tokenize("var s = \"a\\nb\";");

		Assert.Contains(tokens, token => token.Text == "\\n" && token.Scope.StartsWith("constant.character.escape", StringComparison.Ordinal));
		Assert.Contains(tokens, token => token.Text == "a" && token.Scope.StartsWith("string.quoted.double", StringComparison.Ordinal));
	}

	[Fact]
	public void BlockCommentCarriesAcrossLines() {
		List<(string Text, string Scope)> tokens = Tokenize("int a; /* start", "still comment", "end */ int b;");

		Assert.Contains(tokens, token => token.Text == "still comment" && token.Scope.StartsWith("comment.block", StringComparison.Ordinal));
		Assert.Contains(tokens, token => token.Text == "int" && token.Scope.StartsWith("keyword.type", StringComparison.Ordinal));
	}

	[Fact]
	public void SameLineTwiceGivesSameTokens() {
		const string line = "if (value is { Length: > 0 } text) return $\"{text}!\"; // done";

		Assert.Equal(Tokenize(line), Tokenize(line));
	}

	[Fact]
	public void BundledGrammarsUseGeneratedRegexes() {
		TieredRegex? regex = TieredRegex.For(@"\b(class)\b");
		_ = Grammars.ForId("source.cs");

		Assert.NotNull(regex);
	}
}
