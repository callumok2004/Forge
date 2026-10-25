using Forge.Common.Commands;
using Forge.Common.Debug;
using Forge.Debug;
using Forge.Extensions.Dotnet.Debugger;
using Forge.Extensions.Dotnet.Server;

namespace Forge.Tests;

public class DebugValuesTests
{
	[Theory]
	[InlineData("var total = order.Lines.Count;", 20, "order.Lines")]
	[InlineData("var total = order.Lines.Count;", 14, "order")]
	[InlineData("var total = order?.Lines;", 21, "order?.Lines")]
	[InlineData("return this.count + 1;", 13, "this.count")]
	public void ExpressionCoversMemberChainUpToWord(string line, int column, string expected) {
		EvaluatableExpression? expression = DebugExpressions.Evaluatable(line, 1, column, null);

		Assert.Equal(expected, expression?.Expression);
	}

	[Theory]
	[InlineData("Compute(values);", 2)]
	[InlineData("var x = items[0];", 9)]
	[InlineData("if (ready) return;", 1)]
	[InlineData("var s = \"text inside\";", 12)]
	public void CallsKeywordsAndStringsAreNotEvaluated(string line, int column) => Assert.Null(DebugExpressions.Evaluatable(line, 1, column, null));

	[Fact]
	public void InstanceFieldsAreQualifiedWithThis() {
		ServerToken[] tokens = [new(3, 4, 5, "field"), new(3, 12, 6, "field", IsStatic: true)];

		Assert.Equal("this.count", DebugExpressions.Evaluatable("    count = shared;", 3, 6, tokens)?.Expression);
		Assert.Equal("shared", DebugExpressions.Evaluatable("    count = shared;", 3, 14, tokens)?.Expression);
	}

	[Fact]
	public void InlineValuesTakeRootIdentifiersOnly() {
		string[] lines = ["int total = order.Count + Compute(x); // total", "var name = \"order\";"];
		List<InlineValueLookup> lookups = [];

		DebugExpressions.InlineValues(0, 1, line => lines[line], lookups);

		Assert.Equal(["total", "order", "x", "name"], lookups.Select(lookup => lookup.Name));
		Assert.Equal([0, 0, 0, 1], lookups.Select(lookup => lookup.Line));
	}

	[Fact]
	public void TopFrameSkipsFramesWithoutSourceAndSubtleOnPause() {
		string file = typeof(DebugValuesTests).Assembly.Location;
		DebugStackFrame[] frames = [
			new(1, "Thread.Sleep", null, 0, 0, null, null),
			new(2, "Helper", file, 4, 1, "subtle", null),
			new(3, "Main", file, 10, 1, null, null),
		];

		Assert.Equal(3, DebugService.TopStackFrame(frames, "pause")?.Id);
		Assert.Equal(2, DebugService.TopStackFrame(frames, "step")?.Id);
		Assert.Null(DebugService.TopStackFrame([frames[0]], "pause"));
	}
}
