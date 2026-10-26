using Forge.Extensions.Dotnet.Layout;
using Forge.Language;

namespace Forge.Tests;

public class TypeLayoutTests
{
	private const string Path = @"C:\work\Shapes.cs";

	private static async Task<List<DeclarationLayout>> Layouts(string source) {
		LayoutEngine engine = new(null, path => path == Path ? source : null, _ => true);

		return await engine.LayoutsInAsync(Path, CancellationToken.None);
	}

	private static async Task<TypeLayout> Single(string source) => (await Layouts(source)).Single().Layout;

	[Fact]
	public async Task SequentialStructPadsAndSuggestsReorder() {
		TypeLayout layout = await Single("struct Mixed { byte a; long b; byte c; }");

		Assert.Equal(24, layout.Size);
		Assert.Equal(LayoutMode.Sequential, layout.Mode);
		Assert.Equal([0, 8, 16], layout.Fields.Select(field => field.Offset));
		Assert.Equal(7, layout.Fields[1].PadBefore);
		Assert.Equal(16, layout.Reorder?.Size);
		Assert.Equal(["b", "a", "c"], layout.Reorder!.Order.Select(field => field.Name));
	}

	[Fact]
	public async Task ClassHasHeaderAndAutoLayoutReferencesFirst() {
		TypeLayout layout = await Single("class Node { int value; string name; byte flag; }");

		Assert.Equal(LayoutKind.Class, layout.Kind);
		Assert.Equal(LayoutMode.Auto, layout.Mode);
		Assert.Equal(["name", "value", "flag"], layout.Fields.Select(field => field.Name));
		Assert.Equal(13, layout.Size);
		Assert.Equal(32, layout.HeapSize);
	}

	[Fact]
	public async Task EmptyClassIsMinimumObject() => Assert.Equal(LayoutEngine.MinimumObject, (await Single("class Empty { }")).HeapSize);

	[Fact]
	public async Task EnumUsesBaseType() => Assert.Equal(1, (await Single("enum Small : byte { A, B }")).Size);

	[Fact]
	public async Task ExplicitLayoutUsesFieldOffsets() {
		TypeLayout layout = await Single("[StructLayout(LayoutKind.Explicit)] struct Union { [FieldOffset(0)] int i; [FieldOffset(0)] float f; [FieldOffset(4)] long l; }");

		Assert.Equal(LayoutMode.Explicit, layout.Mode);
		Assert.Equal(16, layout.Size);
	}

	[Fact]
	public async Task PackLimitsAlignment() => Assert.Equal(9, (await Single("[StructLayout(LayoutKind.Sequential, Pack = 1)] struct Packed { byte a; long b; }")).Size);

	[Fact]
	public async Task NullableAndTuplesAndArrays() {
		TypeLayout layout = await Single("struct Holder { int? maybe; (int, byte) pair; int[] items; }");

		Assert.Equal([8, 8, 8], layout.Fields.Select(field => field.Size).OrderBy(size => size));
		Assert.True(layout.HasReferences);
	}

	[Fact]
	public async Task GenericDeclarationsAreSkipped() => Assert.Empty(await Layouts("struct Box<T> { T value; }"));

	[Fact]
	public async Task SummaryAndFieldHintsMatchFork() {
		TypeLayout layout = await Single("struct Mixed { byte a; long b; byte c; }");

		Assert.Equal("24 B  ·  14 B wasted  ·  16 B if reordered", TypeLayoutFeature.Summary(layout));
		Assert.Equal("8 B at byte 8  ·  7 B hole before", TypeLayoutFeature.FieldHint(layout.Fields[1]));
	}

	[Fact]
	public async Task HintsLandOnDeclarationAndFieldLines() {
		const string source = "struct Mixed\n{\n\tbyte a;\n\tlong b;\n}";
		LayoutEngine engine = new(null, _ => source, _ => true);
		List<DeclarationLayout> found = await engine.LayoutsInAsync(Path, CancellationToken.None);

		Dictionary<int, Forge.Common.Editor.LineHint> hints = TypeLayoutFeature.BuildHints(found, engine.Document(Path)!, true);

		Assert.Equal([0, 2, 3], hints.Keys.Order());
		Assert.True(hints[3].Warning);
		Assert.False(hints[2].Warning);
	}

	[Fact]
	public async Task TableParsesIntoHeaderRowsAndRules() {
		string table = TypeLayoutFeature.Table(await Single("struct Mixed { byte a; long b; byte c; }"));
		List<HoverWidget.HoverLine> lines = [];

		HoverWidget.Parse(table, lines);

		HoverWidget.HoverLine header = lines.First(line => line.Kind == HoverWidget.LineKind.HeaderRow);
		Assert.Equal(["bytes", "size", "field", "type"], header.Cells!);
		Assert.Contains(lines, line => line.Kind == HoverWidget.LineKind.Rule);
		Assert.Contains(lines, line => line.Kind == HoverWidget.LineKind.Row && line.Cells![2] == "(padding)");
		Assert.DoesNotContain(lines, line => line.Text.StartsWith("|---", StringComparison.Ordinal));
	}

	[Fact]
	public void EscapedPipesStayInCell() {
		List<HoverWidget.HoverLine> lines = [];

		HoverWidget.Parse("| a | b |\n|---|---|\n| `x\\|y` | z |", lines);

		Assert.Equal(["x|y", "z"], lines[1].Cells!);
	}
}
