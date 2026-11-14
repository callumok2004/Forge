using System.Text.Json;

using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Server.Features;

namespace Forge.Tests;

public class RoslynResponseParsingTests
{
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	[Fact]
	public void SemanticTokensDecodeRelativePositionsTypesAndStatic() {
		string[] types = ["namespace", "class", "method", "variable"];
		JsonElement data = Parse("[1,4,5,1,0, 0,6,3,2,2, 2,8,4,3,1, 0,2,0,1,0, 0,1,3,9,0, 1,0,2,2]");

		List<ServerToken> tokens = SemanticTokenRequests.Decode(data, types, staticModifier: 1);

		Assert.Equal([new ServerToken(2, 4, 5, "class"), new ServerToken(2, 10, 3, "method", IsStatic: true), new ServerToken(4, 8, 4, "variable")], tokens);
	}

	[Fact]
	public void SemanticTokensHandleMissingOrShortData() {
		Assert.Empty(SemanticTokenRequests.Decode(null, ["class"], -1));
		Assert.Empty(SemanticTokenRequests.Decode(Parse("[0,0,1]"), ["class"], -1));
		Assert.Empty(SemanticTokenRequests.Decode(Parse("{}"), ["class"], -1));
	}

	[Fact]
	public void CompletionItemsReadEveryFieldAndSort() {
		JsonElement items = Parse("""
			[
				{"label":"Zeta","kind":2,"sortText":"b","detail":"void Zeta()","labelDetails":{"detail":"()","description":"System"},"textEdit":{"range":{"start":{"line":4,"character":2},"end":{"line":4,"character":5}},"newText":"Zeta()"},"preselect":true},
				{"label":"alpha","kind":6,"insertText":"alphaInsert","filterText":"alphaFilter"},
				{"label":"","kind":1},
				{"label":"beta","kind":99,"sortText":"a","textEdit":{"insert":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"replace":{"start":{"line":1,"character":3},"end":{"line":1,"character":7}},"newText":"beta"}}
			]
			""");

		List<ServerCompletion> completions = CompletionRequests.Read(items, null, line: 9, column: 4);

		Assert.Equal(["beta", "alpha", "Zeta"], completions.Select(static c => c.Label));

		ServerCompletion zeta = completions[2];
		Assert.Equal(0, zeta.Index);
		Assert.Equal("()", zeta.LabelDetail);
		Assert.Equal("System", zeta.LabelDescription);
		Assert.Equal("Zeta()", zeta.InsertText);
		Assert.Equal("void Zeta()", zeta.Detail);
		Assert.Equal("Method", zeta.Kind);
		Assert.True(zeta.Preselect);
		Assert.Equal((5, 3, 6), (zeta.ReplaceLine, zeta.ReplaceStartColumn, zeta.ReplaceEndColumn));

		ServerCompletion alpha = completions[1];
		Assert.Equal(1, alpha.Index);
		Assert.Equal("alphaInsert", alpha.InsertText);
		Assert.Equal("alphaFilter", alpha.FilterText);
		Assert.Equal("alpha", alpha.SortText);
		Assert.Equal("Variable", alpha.Kind);
		Assert.Equal((9, 0, 0), (alpha.ReplaceLine, alpha.ReplaceStartColumn, alpha.ReplaceEndColumn));

		ServerCompletion beta = completions[0];
		Assert.Equal(3, beta.Index);
		Assert.Equal("Text", beta.Kind);
		Assert.Equal((2, 4, 8), (beta.ReplaceLine, beta.ReplaceStartColumn, beta.ReplaceEndColumn));
	}

	[Fact]
	public void CompletionFallsBackToItemDefaultsRange() {
		JsonElement items = Parse("""[{"label":"x"}]""");
		JsonElement defaults = Parse("""{"editRange":{"insert":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"replace":{"start":{"line":2,"character":1},"end":{"line":2,"character":6}}}}""");
		JsonElement plain = Parse("""{"editRange":{"start":{"line":3,"character":2},"end":{"line":3,"character":4}}}""");

		ServerCompletion replace = CompletionRequests.Read(items, defaults, 1, 1)[0];
		ServerCompletion range = CompletionRequests.Read(items, plain, 1, 1)[0];

		Assert.Equal((3, 2, 7), (replace.ReplaceLine, replace.ReplaceStartColumn, replace.ReplaceEndColumn));
		Assert.Equal((4, 3, 5), (range.ReplaceLine, range.ReplaceStartColumn, range.ReplaceEndColumn));
	}

	[Fact]
	public void CompletionRangeWithoutEndUsesCaretColumn() {
		JsonElement items = Parse("""[{"label":"x","textEdit":{"range":{"start":{"line":0,"character":2}},"newText":"x"}}]""");

		ServerCompletion completion = CompletionRequests.Read(items, null, 1, 12)[0];

		Assert.Equal((1, 3, 12), (completion.ReplaceLine, completion.ReplaceStartColumn, completion.ReplaceEndColumn));
	}
}
