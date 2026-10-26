using Forge.Extensions.DotnetProfiler;
using Forge.Extensions.DotnetProfiler.Engine;

namespace Forge.Tests;

public class DotnetProfilerTests
{
	[Theory]
	[InlineData("MyApp.Core", "MyApp.Core.Service.Run()", true)]
	[InlineData("MyApp.Core", "MyApp.Core", true)]
	[InlineData("MyApp.Core", "MyApp.CoreExtras.Run()", false)]
	[InlineData("MyApp.Core.*", "MyApp.Core.Service.Run()", true)]
	[InlineData("myapp.core", "MyApp.Core+Nested.Run()", true)]
	[InlineData("MyApp.*.Service", "MyApp.Core.Service.Run()", true)]
	[InlineData("MyApp.*.Service", "MyApp.Core.Deep.Service.Run()", false)]
	[InlineData("MyApp.C?re", "MyApp.Core.Run()", true)]
	[InlineData("Other; MyApp", "MyApp.Run()", true)]
	public void NamespaceFilterMatchesBoundaries(string filter, string name, bool expected) => Assert.Equal(expected, NamespaceFilter.Parse(filter)!.Matches(name));

	[Theory]
	[InlineData("")]
	[InlineData("  ;  , ")]
	[InlineData("*")]
	public void EmptyFiltersMeanNoFilter(string filter) => Assert.Null(NamespaceFilter.Parse(filter));

	[Theory]
	[InlineData("App.Worker.Run(int)", "App.Worker.Run")]
	[InlineData("App.List`1[System.Int32].Add(!0)", "App.List.Add")]
	[InlineData("App.Worker+<>c__DisplayClass3_0.<Run>b__0()", "App.Worker.Run")]
	[InlineData("App.Worker.<Run>b__4_0(int)", "App.Worker.Run")]
	[InlineData("App.Worker.<Run>g__Local|2_0()", "App.Worker.Run")]
	[InlineData("App.Worker+<LoadAsync>d__7.MoveNext()", "App.Worker.LoadAsync")]
	[InlineData("App.Worker+<>c.Helper()", "App.Worker.Helper")]
	public void NormalizeCollapsesCompilerGeneratedNames(string name, string expected) => Assert.Equal(expected, FrameNames.Normalize(name));

	[Theory]
	[InlineData("System.Collections.Generic.List.Add(int)", "List.Add")]
	[InlineData("App.Run", "App.Run")]
	[InlineData("Run", "Run")]
	public void ShortKeepsLastTwoSegments(string name, string expected) => Assert.Equal(expected, FrameNames.Short(name));

	private static ProfileTree Tree() {
		CallTree tree = new();
		int main = tree.Frames.Intern("app", "App.Main()");
		int work = tree.Frames.Intern("app", "App.Work()");
		int add = tree.Frames.Intern("System.Private.CoreLib", "System.Collections.Generic.List.Add(int)");
		int idle = tree.Frames.Intern("app", "App.Idle()");

		for (int i = 0; i < 6; i++) tree.AddSample([add, work, main]);

		for (int i = 0; i < 3; i++) tree.AddSample([work, main]);

		tree.AddSample([idle, main]);
		tree.AddSample([work, work, main]);

		return tree.Snapshot();
	}

	[Fact]
	public void SnapshotOrdersChildrenByTotalDescending() {
		ProfileTree tree = Tree();
		int main = tree.FirstChild[0];

		Assert.Equal("App.Main()", tree.Name(main));
		Assert.Equal(11, tree.Total[main]);
		Assert.Equal("App.Work()", tree.Name(tree.FirstChild[main]));
		Assert.Equal("App.Idle()", tree.Name(tree.NextSibling[tree.FirstChild[main]]));
	}

	[Fact]
	public void FunctionIndexComputesOwnSelfAndTotalWithoutDoubleCountingRecursion() {
		FunctionIndex index = new(Tree(), grouped: true);
		FunctionRow work = index.Rows.Single(row => row.Name == "App.Work");
		FunctionRow add = index.Rows.Single(row => row.Name.EndsWith("List.Add", StringComparison.Ordinal));

		Assert.Equal(10, work.Total);
		Assert.Equal(4, work.Self);
		Assert.Equal(10, work.Own);
		Assert.Equal(6, add.Self);
		Assert.Equal(0, add.Own);
		Assert.True(add.System);
	}

	[Fact]
	public void CallersAndCalleesAttributeEdges() {
		FunctionIndex index = new(Tree(), grouped: true);
		FunctionRow work = index.Rows.Single(row => row.Name == "App.Work");
		FunctionRow main = index.Rows.Single(row => row.Name == "App.Main");

		Assert.Equal("App.Main", Assert.Single(index.Callers(work)).Name);

		List<Edge> callees = index.Callees(main);
		Assert.Equal("App.Work", callees[0].Name);
		Assert.Equal(10, callees[0].Weight);
		Assert.Equal(FunctionIndex.SelfName, index.Callees(work)[0].Name);
		Assert.Equal(new[] { "App.Main()", "App.Work()" }, index.HotPath().Take(2).Select(node => index.Tree.Name(node)));
	}

	[Fact]
	public void DiffReportsGrowthShrinkAndRemovedTypes() {
		HeapSnapshot before = Snapshot("a", ("Foo", 10, 100), ("Bar", 5, 50), ("Gone", 1, 10));
		HeapSnapshot after = Snapshot("b", ("Foo", 20, 300), ("Bar", 2, 20), ("New", 3, 30));

		SnapshotDiff diff = HeapSnapshots.Diff(before, after);

		Assert.Equal(["Foo", "New", "Bar", "Gone"], diff.Changes.Select(change => change.TypeName));
		Assert.Equal(190, diff.ByteDelta);
		Assert.Equal(200, diff.Changes[0].ByteDelta);
	}

	private static HeapSnapshot Snapshot(string id, params (string Name, long Count, long Bytes)[] types) =>
		new(id, 1, DateTimeOffset.Now, types.Sum(type => type.Count), types.Sum(type => type.Bytes), types.Length, [.. types.Select(type => new TypeStat(type.Name, type.Count, type.Bytes))], [], 0, null);

	[Theory]
	[InlineData("\"C:\\Program Files\\dotnet\\dotnet.exe\" exec \"C:\\app\\My App.dll\" --x", "My App")]
	[InlineData("dotnet run", null)]
	[InlineData("dotnet bin/Debug/Server.dll", "Server")]
	public void ManagedAssemblyFromCommandLine(string commandLine, string? expected) => Assert.Equal(expected, ProcessDiscovery.ManagedAssembly(commandLine));

	[Theory]
	[InlineData("App.Worker.Run(int)", "Worker", "Run")]
	[InlineData("App.Worker..ctor()", "Worker", "Worker")]
	[InlineData("App.Outer+Inner.Go()", "Inner", "Go")]
	public void SplitNameFindsTypeAndMethod(string name, string type, string method) => Assert.Equal((type, method), SourceAnnotations.SplitName(name));

	[Fact]
	public void CounterHistoryRingKeepsOrderAndMarks() {
		CounterHistory history = new();

		for (int i = 0; i < CounterHistory.Capacity + 5; i++) {
			if (i == CounterHistory.Capacity + 4) history.Mark(2);

			history.Push(i);
		}

		Assert.Equal(CounterHistory.Capacity, history.Count);
		Assert.Equal(5, history.At(0));
		Assert.Equal(CounterHistory.Capacity + 4, history.At(CounterHistory.Capacity - 1));
		Assert.Equal(3, history.MarkAt(CounterHistory.Capacity - 1));
	}
}
