using System.Diagnostics;

using Forge.Syntax;
using Forge.Text;

namespace Forge.Benchmarks;

public static class GrammarStartup
{
	public static void Run() {
		TextSnapshot snapshot = new TextBuffer(RepositorySource.Read("Forge.Editor", "TextView.cs")).CreateSnapshot();
		Stopwatch watch = Stopwatch.StartNew();
		Highlighter highlighter = new(Languages.CSharp);

		for (int line = 0; line < 60; line++) highlighter.ColorLine(snapshot, line);

		Console.WriteLine($"First 60 lines, fresh process: {watch.Elapsed.TotalMilliseconds:F1} ms");

		for (int pass = 0; pass < 5; pass++) {
			watch.Restart();
			Highlighter fresh = new(Languages.CSharp);

			for (int line = 0; line < snapshot.LineCount; line++) fresh.ColorLine(snapshot, line);

			Console.WriteLine($"Whole file pass {pass}: {watch.Elapsed.TotalMilliseconds:F1} ms");
		}
	}
}
