using BenchmarkDotNet.Attributes;
using System.Text;

using Forge.Text;

namespace Forge.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class EditingBenchmarks
{
	private const int Edits = 1000;

	private string _text = string.Empty;
	private TextBuffer _buffer = null!;
	private StringBuilder _builder = null!;

	[Params(50_000)]
	public int Lines { get; set; }

	[GlobalSetup]
	public void Setup() => _text = DocumentSource.Code(Lines);

	[IterationSetup]
	public void Reset() {
		_buffer = new TextBuffer(_text);
		_builder = new StringBuilder(_text);
	}

	[Benchmark(Description = "Type 1000 chars at end")]
	public int TypeAtEnd() {
		for (int i = 0; i < Edits; i++) _buffer.Insert(_buffer.Length, "x");

		return _buffer.Length;
	}

	[Benchmark(Description = "Type 1000 chars mid-document")]
	public int TypeInMiddle() {
		int offset = _buffer.Length / 2;
		for (int i = 0; i < Edits; i++) _buffer.Insert(offset + i, "x");

		return _buffer.Length;
	}

	[Benchmark(Description = "Type 1000 chars at scattered offsets")]
	public int TypeScattered() {
		Random random = new(7);
		for (int i = 0; i < Edits; i++) _buffer.Insert(random.Next(_buffer.Length), "x");

		return _buffer.Length;
	}

	[Benchmark(Description = "Backspace 1000 times mid-document")]
	public int Backspace() {
		int offset = _buffer.Length / 2;
		for (int i = 0; i < Edits; i++) _buffer.Delete(offset - i, 1);

		return _buffer.Length;
	}

	[Benchmark(Baseline = true, Description = "StringBuilder: type 1000 chars mid-document")]
	public int StringBuilderBaseline() {
		int offset = _builder.Length / 2;
		for (int i = 0; i < Edits; i++) _builder.Insert(offset + i, 'x');

		return _builder.Length;
	}
}

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class SingleEditBenchmarks
{
	private const int Batch = 1000;
	private const int FragmentEdits = 5000;

	private string _text = string.Empty;
	private TextBuffer _clean = null!;
	private TextBuffer _fragmented = null!;

	[Params(50_000)]
	public int Lines { get; set; }

	[GlobalSetup]
	public void Setup() => _text = DocumentSource.Code(Lines);

	[IterationSetup]
	public void Reset() {
		_clean = new TextBuffer(_text);

		_fragmented = new TextBuffer(_text);
		Random random = new(11);
		for (int i = 0; i < FragmentEdits; i++) _fragmented.Insert(random.Next(_fragmented.Length), "z");
	}

	[Benchmark(Description = "1000 insert+delete round trips")]
	public int RoundTrip() {
		int offset = _clean.Length / 2;
		for (int i = 0; i < Batch; i++) {
			_clean.Insert(offset, "x");
			_clean.Delete(offset, 1);
		}

		return _clean.Length;
	}

	[Benchmark(Description = "1000 insert+delete round trips (fragmented)")]
	public int RoundTripFragmented() {
		int offset = _fragmented.Length / 2;
		for (int i = 0; i < Batch; i++) {
			_fragmented.Insert(offset, "x");
			_fragmented.Delete(offset, 1);
		}

		return _fragmented.Length;
	}

	[Benchmark(Description = "1000 append+delete round trips (typing fast path)")]
	public int AppendRoundTrip() {
		for (int i = 0; i < Batch; i++) {
			_clean.Insert(_clean.Length, "x");
			_clean.Delete(_clean.Length - 1, 1);
		}

		return _clean.Length;
	}
}
