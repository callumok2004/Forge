using BenchmarkDotNet.Attributes;

using Forge.Text;

namespace Forge.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class LoadBenchmarks
{
	private byte[] _utf8 = [];

	[Params(50_000)]
	public int Lines { get; set; }

	[GlobalSetup]
	public void Setup() => _utf8 = DocumentSource.Utf8(Lines);

	[Benchmark(Description = "TextLoader.Load")]
	public int Load() {
		using MemoryStream stream = new(_utf8, writable: false);
		return TextLoader.Load(stream, _utf8.Length).Length;
	}

	[Benchmark(Baseline = true, Description = "StreamReader.ReadToEnd (naive)")]
	public int ReadToEnd() {
		using MemoryStream stream = new(_utf8, writable: false);
		using StreamReader reader = new(stream);
		return reader.ReadToEnd().Length;
	}
}

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class SnapshotBenchmarks
{
	private TextBuffer _clean = null!;
	private TextBuffer _fragmented = null!;

	[Params(50_000)]
	public int Lines { get; set; }

	[GlobalSetup]
	public void Setup() {
		_clean = new TextBuffer(DocumentSource.Code(Lines));

		_fragmented = new TextBuffer(DocumentSource.Code(Lines));
		Random random = new(5);
		for (int i = 0; i < 10_000; i++) _fragmented.Insert(random.Next(_fragmented.Length), "z");
	}

	[Benchmark(Description = "Snapshot a freshly loaded document")]
	public int SnapshotClean() {
		_clean.Insert(0, "x");
		int length = _clean.CreateSnapshot().Length;
		_clean.Delete(0, 1);
		return length;
	}

	[Benchmark(Description = "Snapshot after 10k scattered edits")]
	public int SnapshotFragmented() {
		_fragmented.Insert(0, "x");
		int length = _fragmented.CreateSnapshot().Length;
		_fragmented.Delete(0, 1);
		return length;
	}

	[Benchmark(Description = "Read whole document through a snapshot")]
	public int ReadAll() => _clean.CreateSnapshot().GetText().Length;
}
