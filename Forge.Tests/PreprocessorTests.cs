using System.Diagnostics;
using System.Text;

using Forge.Extensions.Dotnet.Language;

namespace Forge.Tests;

public class PreprocessorTests(ITestOutputHelper output)
{
	private static string Sample(int blocks) {
		StringBuilder text = new();

		for (int i = 0; i < blocks; i++) {
			text.Append("class A").Append(i).Append(" {\n#if GAME_DLL\n\tint server;\n#elif CLIENT_DLL\n\tint client;\n#if DEBUG && !X\n\tint debug;\n#else\n\tint release;\n#endif\n#else\n\tint other;\n#endif\n\tint shared;\n}\n");
		}

		return text.ToString();
	}

	[Theory]
	[InlineData("GAME_DLL")]
	[InlineData("CLIENT_DLL", "DEBUG")]
	[InlineData("CLIENT_DLL")]
	[InlineData()]
	public void ActiveLinesMatchesIsActiveAt(params string[] defines) {
		string text = Sample(20);
		HashSet<string> set = [.. defines];
		bool[] active = Preprocessor.ActiveLines(text, set);
		int lines = text.Split('\n').Length;

		Assert.Equal(lines, active.Length);

		for (int line = 0; line < lines; line++) Assert.Equal(Preprocessor.IsActiveAt(text, line, set), active[line]);
	}

	[Fact]
	public void ActiveLinesIsFasterThanPerLineScan() {
		string text = Sample(400);
		HashSet<string> set = ["CLIENT_DLL"];
		int diagnostics = 500;

		Stopwatch watch = Stopwatch.StartNew();

		for (int i = 0; i < diagnostics; i++) Preprocessor.IsActiveAt(text, i * 11, set);

		TimeSpan old = watch.Elapsed;
		watch.Restart();
		bool[] active = Preprocessor.ActiveLines(text, set);

		for (int i = 0; i < diagnostics; i++) _ = active[i * 11];

		TimeSpan current = watch.Elapsed;
		output.WriteLine($"{diagnostics} diagnostics over {active.Length} lines: per-line {old.TotalMilliseconds:F1}ms, single pass {current.TotalMilliseconds:F1}ms");

		Assert.True(current < old);
	}
}
