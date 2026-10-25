using BenchmarkDotNet.Attributes;

using Forge.Syntax;
using Forge.Text;

namespace Forge.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class SyntaxBenchmarks
{
	private TextSnapshot _snapshot = null!;

	[GlobalSetup]
	public void Setup() {
		_snapshot = new TextBuffer(RepositorySource.Read("Forge.Editor", "TextView.cs")).CreateSnapshot();
		new Highlighter(Languages.CSharp).ColorLine(_snapshot, 0);
	}

	[Benchmark(Description = "TextMate colour whole file (cold)")]
	public int ColorFile() {
		Highlighter highlighter = new(Languages.CSharp);
		int tokens = 0;

		for (int line = 0; line < _snapshot.LineCount; line++) tokens += highlighter.ColorLine(_snapshot, line)!.Count;

		return tokens;
	}

	[Benchmark(Description = "Fallback tokenizer whole file")]
	public int TokenizeFile() {
		Highlighter highlighter = new(Languages.CSharp);
		int tokens = 0;

		for (int line = 0; line < _snapshot.LineCount; line++) tokens += highlighter.TokenizeLine(_snapshot, line).Count;

		return tokens;
	}
}

internal static class RepositorySource
{
	public static string Read(params string[] parts) {
		string? directory = AppContext.BaseDirectory;

		while (directory is not null && !File.Exists(Path.Combine(directory, "Forge.slnx"))) directory = Path.GetDirectoryName(directory);

		return File.ReadAllText(Path.Combine([directory ?? throw new DirectoryNotFoundException("Forge.slnx"), .. parts]));
	}
}
