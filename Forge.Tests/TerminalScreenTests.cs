using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.Dotnet.Build;
using Forge.Terminal;

namespace Forge.Tests;

public class TerminalScreenTests
{
	private const string Long = "word00xxxxxword01xxxxxword02xxxxxword03xxxxxword04xxxxxword05xxxxxword06xxxxx";

	private static TerminalScreen Screen(int columns, int rows = 10) {
		TerminalScreen screen = new();
		screen.Resize(columns, rows);
		return screen;
	}

	private static List<string> Fed(TerminalScreen screen) {
		List<string> lines = [];
		screen.LineFed += lines.Add;
		return lines;
	}

	private static int FirstLine(TerminalScreen screen) => screen.LineIndex(0);

	[Fact]
	public void LongLineIsOneLogicalLine() {
		TerminalScreen screen = Screen(20);
		screen.Write(Long);

		Assert.Equal(Long, screen.LogicalText(FirstLine(screen)));
		Assert.Equal(Long, screen.LogicalText(FirstLine(screen) + 2));
	}

	[Fact]
	public void WrappedRowsAreFlagged() {
		TerminalScreen screen = Screen(20);
		screen.Write(Long);
		int first = FirstLine(screen);

		Assert.True(screen.IsWrapped(first));
		Assert.True(screen.IsWrapped(first + 2));
		Assert.False(screen.IsWrapped(first + 3));
	}

	[Fact]
	public void TextAcrossWrappedRowsHasNoNewline() {
		TerminalScreen screen = Screen(20);
		screen.Write(Long);
		int first = FirstLine(screen);

		Assert.Equal(Long, screen.Text(first, 0, first + 3, 20));
	}

	[Fact]
	public void TextAcrossHardLinesHasNewline() {
		TerminalScreen screen = Screen(20);
		screen.Write("alpha\r\nbeta");
		int first = FirstLine(screen);

		Assert.Equal($"alpha{Environment.NewLine}beta", screen.Text(first, 0, first + 1, 20));
	}

	[Fact]
	public void PartialSelectionInsideWrappedLine() {
		TerminalScreen screen = Screen(10);
		screen.Write("0123456789abcdefghij");
		int first = FirstLine(screen);

		Assert.Equal("789abc", screen.Text(first, 7, first + 1, 3));
	}

	[Fact]
	public void ExactWidthLineIsNotJoinedWithNext() {
		TerminalScreen screen = Screen(10);
		screen.Write("0123456789\r\nnext");
		int first = FirstLine(screen);

		Assert.False(screen.IsWrapped(first));
		Assert.Equal("0123456789", screen.LogicalText(first));
		Assert.Equal("next", screen.LogicalText(first + 1));
	}

	[Fact]
	public void LineFedJoinsWrappedRows() {
		TerminalScreen screen = Screen(20);
		List<string> lines = Fed(screen);

		screen.Write(Long + "\r\n");

		Assert.Equal([Long], lines);
	}

	[Fact]
	public void LineFedReportsEveryLine() {
		TerminalScreen screen = Screen(20);
		List<string> lines = Fed(screen);

		screen.Write("one\r\ntwo\r\n\r\nthree\r\n");

		Assert.Equal(["one", "two", "", "three"], lines);
	}

	[Fact]
	public void LineFedHandlesBareLineFeed() {
		TerminalScreen screen = Screen(20);
		List<string> lines = Fed(screen);

		screen.Write("one\ntwo\n");

		Assert.Equal("one", lines[0]);
		Assert.Equal(2, lines.Count);
	}

	[Fact]
	public void LineFedStripsColours() {
		TerminalScreen screen = Screen(40);
		List<string> lines = Fed(screen);

		screen.Write("\u001b[33;1mwarning\u001b[0m CS0168: unused\r\n");

		Assert.Equal(["warning CS0168: unused"], lines);
	}

	[Fact]
	public void LineFedIgnoresTitleSequence() {
		TerminalScreen screen = Screen(40);
		List<string> lines = Fed(screen);

		screen.Write("\u001b]0;dotnet build\u0007visible\r\n");

		Assert.Equal(["visible"], lines);
	}

	[Fact]
	public void LineFedIgnoresProgressSequence() {
		TerminalScreen screen = Screen(40);
		List<string> lines = Fed(screen);

		screen.Write("\u001b]9;4;3;\u001b\\Build succeeded\r\n");

		Assert.Equal(["Build succeeded"], lines);
	}

	[Fact]
	public void LineFedKeepsHyperlinkTextOnly() {
		TerminalScreen screen = Screen(40);
		List<string> lines = Fed(screen);

		screen.Write("out \u001b]8;;file:///C:/bin/App.dll\u001b\\App.dll\u001b]8;;\u001b\\\r\n");

		Assert.Equal(["out App.dll"], lines);
	}

	[Fact]
	public void CarriageReturnOverwriteReportsFinalText() {
		TerminalScreen screen = Screen(40);
		List<string> lines = Fed(screen);

		screen.Write("progress 10%\rprogress 100%\r\n");

		Assert.Equal(["progress 100%"], lines);
	}

	[Fact]
	public void EraseLineThenRewriteReportsRewrite() {
		TerminalScreen screen = Screen(40);
		List<string> lines = Fed(screen);

		screen.Write("building...\r\u001b[2Kdone\r\n");

		Assert.Equal(["done"], lines);
	}

	[Fact]
	public void TrailingSpacesTrimmed() {
		TerminalScreen screen = Screen(20);
		List<string> lines = Fed(screen);

		screen.Write("text     \r\n");

		Assert.Equal(["text"], lines);
	}

	[Fact]
	public void LineFedJoinsAfterNarrowResize() {
		TerminalScreen screen = Screen(80);
		List<string> lines = Fed(screen);

		screen.Write(Long.AsSpan(0, 30));
		screen.Resize(12, 10);
		screen.Write(string.Concat(Long.AsSpan(30), "\r\n"));

		Assert.Equal([Long], lines);
	}

	[Fact]
	public void ResizeNarrowerKeepsLogicalLine() {
		TerminalScreen screen = Screen(80);
		screen.Write(Long + "\r\nafter");
		screen.Resize(13, 10);

		int index = FindLine(screen, "word00");

		Assert.Equal(Long, screen.LogicalText(index));
	}

	[Fact]
	public void ResizeWiderUnwraps() {
		TerminalScreen screen = Screen(13);
		screen.Write(Long + "\r\nafter");
		screen.Resize(200, 10);

		int index = FindLine(screen, "word00");

		Assert.False(screen.IsWrapped(index));
		Assert.Equal(Long, screen.LogicalText(index));
		Assert.Equal("after", screen.LogicalText(index + 1));
	}

	[Fact]
	public void ResizeRoundTripPreservesText() {
		TerminalScreen screen = Screen(30);
		screen.Write("first line\r\n" + Long + "\r\nlast");
		screen.Resize(7, 10);
		screen.Resize(23, 10);
		screen.Resize(30, 10);

		int index = FindLine(screen, "first");

		Assert.Equal("first line", screen.LogicalText(index));
		Assert.Equal(Long, screen.LogicalText(index + 1));
		Assert.Equal("last", screen.LogicalText(screen.LogicalLine(index + 1).Last + 1));
	}

	[Fact]
	public void BuildWarningMatchesThroughNarrowTerminal() {
		TerminalScreen screen = Screen(40);
		List<string> lines = Fed(screen);
		const string Warning = @"    C:\Users\me\source\repos\Source.NET\Source.Launcher\Program.cs(48,7): warning CS0649: Field 'Bootloader.isEditMode' is never assigned to, and will always have its default value false";

		screen.Write("\u001b[33m" + Warning + "\u001b[0m\r\n");

		Assert.Single(lines);
		Diagnostic? diagnostic = MsBuildProblemMatcher.Parse(lines[0]);

		Assert.NotNull(diagnostic);
		Assert.Equal(@"C:\Users\me\source\repos\Source.NET\Source.Launcher\Program.cs", diagnostic.Value.File);
		Assert.Equal(48, diagnostic.Value.Line);
		Assert.Equal(7, diagnostic.Value.Column);
		Assert.Equal("CS0649", diagnostic.Value.Code);
	}

	[Fact]
	public void ManyBuildLinesAllObserved() {
		TerminalScreen screen = Screen(30, 5);
		List<string> lines = Fed(screen);

		for (int i = 0; i < 200; i++) screen.Write($@"C:\src\File{i}.cs({i + 1},1): warning CS0168: unused variable number {i}" + "\r\n");

		Assert.Equal(200, lines.Count);
		Assert.All(lines, line => Assert.NotNull(MsBuildProblemMatcher.Parse(line)));
	}

	[Fact]
	public void SplitWritesJoin() {
		TerminalScreen screen = Screen(20);
		List<string> lines = Fed(screen);

		screen.Write("hello ");
		screen.Write("\u001b[3");
		screen.Write("2mworld");
		screen.Write("\r");
		screen.Write("\n");

		Assert.Equal(["hello world"], lines);
	}

	private static string Visible(TerminalScreen screen, int row) => screen.LogicalText(screen.LineIndex(row));

	private static TerminalScreen Scrolled(int columns, int count = 60) {
		TerminalScreen screen = Screen(columns, 5);

		for (int i = 0; i < count; i++) screen.Write($"line {i:D2} " + Long + "\r\n");

		return screen;
	}

	[Fact]
	public void ScrolledViewKeepsTopLineWhenNarrowed() {
		TerminalScreen screen = Scrolled(100);
		screen.ScrollBy(20);
		string top = Visible(screen, 0);

		screen.Resize(17, 5);

		Assert.Equal(top, Visible(screen, 0));
		Assert.True(screen.ScrollOffset > 0);
	}

	[Fact]
	public void ScrolledViewKeepsTopLineWhenWidened() {
		TerminalScreen screen = Scrolled(17);
		screen.ScrollBy(40);
		string top = Visible(screen, 0);

		screen.Resize(120, 5);

		Assert.Equal(top, Visible(screen, 0));
	}

	[Fact]
	public void RepeatedResizeWhileScrolledNeverBreaksLines() {
		TerminalScreen screen = Scrolled(50);
		screen.ScrollBy(15);

		foreach (int width in (int[])[13, 77, 9, 31, 200, 50]) {
			screen.Resize(width, 5);

			for (int row = 0; row < screen.Rows; row++) {
				string text = Visible(screen, row);

				if (text.Length > 0) Assert.Matches(@"^line \d\d " + Long + "$", text);
			}
		}
	}

	[Fact]
	public void EveryLineSurvivesResizeAtBottom() {
		TerminalScreen screen = Scrolled(40, 30);

		screen.Resize(11, 5);
		screen.Resize(90, 5);

		List<string> all = [];

		for (int i = 0; i < screen.LineCount; i = screen.LogicalLine(i).Last + 1) {
			string text = screen.LogicalText(i);

			if (text.Length > 0) all.Add(text);
		}

		Assert.Equal(30, all.Count);

		for (int i = 0; i < 30; i++) Assert.Equal($"line {i:D2} " + Long, all[i]);
	}

	[Fact]
	public void VisibleRowsOfWrappedLineAreContiguous() {
		TerminalScreen screen = Screen(10, 10);
		screen.Write("0123456789abcdefghijKLMNO\r\n");
		screen.Resize(7, 10);

		string joined = string.Concat(Enumerable.Range(0, 4).Select(row => new string([.. screen.Row(row).ToArray().Select(static cell => cell.Character)])));

		Assert.StartsWith("0123456789abcdefghijKLMNO", joined, StringComparison.Ordinal);
	}

	[Fact]
	public void OutputWhileScrolledKeepsViewStable() {
		TerminalScreen screen = Scrolled(30);
		screen.ScrollBy(10);
		string top = Visible(screen, 0);

		screen.Write("more output\r\n" + Long + "\r\n");

		Assert.Equal(top, Visible(screen, 0));
	}

	private static int FindLine(TerminalScreen screen, string start) {
		for (int i = 0; i < screen.LineCount; i++) {
			if (screen.LogicalText(i).StartsWith(start, StringComparison.Ordinal)) return i;
		}

		return -1;
	}
}
