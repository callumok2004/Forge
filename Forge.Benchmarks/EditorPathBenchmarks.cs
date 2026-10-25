using BenchmarkDotNet.Attributes;

using Forge.Common.Commands;
using Forge.Language;
using Forge.Terminal;
using Forge.Text;
using Forge.Workspace;

namespace Forge.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class EditorPathBenchmarks
{
	private readonly string[] _labels = new string[2000];
	private readonly HostTextEdit[] _edits = new HostTextEdit[500];
	private string _document = string.Empty;
	private string _terminalOutput = string.Empty;
	private TerminalScreen _screen = null!;

	[GlobalSetup]
	public void Setup() {
		Random random = new(5);
		string[] parts = ["Get", "Set", "Line", "Span", "Text", "View", "Buffer", "Caret", "Async", "Snapshot", "Count", "Offset"];

		for (int i = 0; i < _labels.Length; i++) _labels[i] = parts[random.Next(parts.Length)] + parts[random.Next(parts.Length)] + parts[random.Next(parts.Length)];

		_document = DocumentSource.Code(5_000);

		for (int i = 0; i < _edits.Length; i++) _edits[i] = new HostTextEdit((i * 10) + 1, 1, (i * 10) + 1, 2, "    ");

		System.Text.StringBuilder output = new();

		for (int i = 0; i < 2000; i++) output.Append($"\u001b[32mPASS\u001b[0m test_{i} \u001b[1;34m({i % 97} ms)\u001b[0m\r\n");

		_terminalOutput = output.ToString();
		_screen = new TerminalScreen();
	}

	[Benchmark(Description = "Suggest score 2000 items, 3-char word")]
	public int SuggestScore() {
		Span<int> matches = stackalloc int[FuzzyScore.MaxMatches];
		int hits = 0;

		foreach (string label in _labels)
			if (FuzzyScore.TryScore("gls", label, out _, matches, out _)) hits++;

		return hits;
	}

	[Benchmark(Description = "Apply 500 formatting edits")]
	public int ApplyEdits() {
		TextBuffer buffer = new(_document);
		DocumentEdits.Apply(buffer, _edits, out _, out int delta);

		return delta;
	}

	[Benchmark(Description = "Terminal: write 2000 coloured lines")]
	public int TerminalWrite() {
		_screen.Write(_terminalOutput);

		return _screen.CursorRow;
	}
}
