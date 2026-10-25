using BenchmarkDotNet.Attributes;

using Forge.Language;
using Forge.Search;

namespace Forge.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class QuickOpenBenchmarks
{
	private readonly FuzzyMatch[] _results = new FuzzyMatch[64];

	private string[] _files = [];

	[Params(50_000)]
	public int Files { get; set; }

	[GlobalSetup]
	public void Setup() {
		Random random = new(3);
		string[] folders = ["src", "Forge.Editor", "Forge.Shell", "tests", "Actions", "Sidebar", "native", "obj"];
		_files = new string[Files];

		for (int i = 0; i < Files; i++) _files[i] = $"{folders[random.Next(folders.Length)]}/{folders[random.Next(folders.Length)]}/File{i}.cs";
	}

	[Benchmark(Description = "Rank 50k paths, 3-char query")]
	public int RankShort() => FuzzyMatcher.Rank("wbc", _files, _results);

	[Benchmark(Description = "Rank 50k paths, 12-char query")]
	public int RankLong() => FuzzyMatcher.Rank("shellsidebar", _files, _results);

	[Benchmark(Description = "Hover markdown inline x1000")]
	public int HoverInline() {
		int total = 0;

		for (int i = 0; i < 1000; i++) total += HoverWidget.Inline(@"(method) void System\.Collections\.Generic\.List<T>\.Add(T item) - **Adds** an `object`").Length;

		return total;
	}
}
