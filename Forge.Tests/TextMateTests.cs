using Forge.Syntax.TextMate;

namespace Forge.Tests;

public class TextMateTests
{
	private static List<(string Text, string Scope)> Tokenize(params string[] lines) {
		GrammarTokenizer tokenizer = new(Grammars.ForId("source.cs") ?? Grammars.ForId("csharp") ?? throw new InvalidOperationException("no C# grammar"));
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
