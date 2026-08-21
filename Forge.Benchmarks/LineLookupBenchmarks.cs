using BenchmarkDotNet.Attributes;

using Forge.Text;

namespace Forge.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class LineLookupBenchmarks
{
	private TextBuffer _buffer = null!;
	private TextSnapshot _snapshot = null!;
	private TextBuffer _fragmented = null!;
	private int[] _lines = [];
	private char[] _scratch = [];

	[Params(50_000)]
	public int Lines { get; set; }

	[GlobalSetup]
	public void Setup() {
		_buffer = new TextBuffer(DocumentSource.Code(Lines));
		_snapshot = _buffer.CreateSnapshot();
		_scratch = new char[4096];

		Random random = new(3);
		_lines = new int[80];
		for (int i = 0; i < _lines.Length; i++) _lines[i] = random.Next(_buffer.LineCount);

		_fragmented = new TextBuffer(DocumentSource.Code(Lines));
		for (int i = 0; i < 5000; i++) _fragmented.Insert(random.Next(_fragmented.Length), "z");
	}

	[Benchmark(Description = "Buffer: line -> offset x80")]
	public int LineToOffset() {
		int total = 0;
		for (int i = 0; i < _lines.Length; i++) total += _buffer.GetLineStartOffset(_lines[i]);

		return total;
	}

	[Benchmark(Description = "Buffer: line -> offset x80 (fragmented)")]
	public int LineToOffsetFragmented() {
		int total = 0;
		for (int i = 0; i < _lines.Length; i++) total += _fragmented.GetLineStartOffset(_lines[i]);

		return total;
	}

	[Benchmark(Description = "Snapshot: line -> offset x80")]
	public int SnapshotLineToOffset() {
		int total = 0;
		for (int i = 0; i < _lines.Length; i++) total += _snapshot.GetLineStartOffset(_lines[i]);

		return total;
	}

	[Benchmark(Description = "Buffer: offset -> line x80")]
	public int OffsetToLine() {
		int total = 0;
		for (int i = 0; i < _lines.Length; i++) total += _buffer.GetLineAt(_lines[i] * 7);

		return total;
	}

	[Benchmark(Description = "Render a viewport: 80 lines into a pooled buffer")]
	public int RenderViewport() {
		int total = 0;
		for (int i = 0; i < _lines.Length; i++) total += _snapshot.CopyLineTo(_lines[i], _scratch);

		return total;
	}

	[Benchmark(Description = "Render a viewport: 80 lines as strings (naive)")]
	public int RenderViewportAllocating() {
		int total = 0;
		for (int i = 0; i < _lines.Length; i++) {
			total += _snapshot.GetText(_snapshot.GetLineSpan(_lines[i])).Length;
		}

		return total;
	}
}
