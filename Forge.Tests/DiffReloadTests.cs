using Forge.Editor;
using Forge.Text;

namespace Forge.Tests;

public class DiffReloadTests
{
	[Fact]
	public void ExternalBufferChangesRefreshTheViewSnapshot() {
		TextView view = new(new TextBuffer("a\nb\nc\nd"));
		view.Buffer.ReplaceChanged(view.Buffer.GetText(), "a\nc\nd");

		Assert.Equal(3, view.Snapshot.LineCount);
		Assert.Equal(view.Buffer.Version, view.Snapshot.Version);
	}

	[Fact]
	public void DiffMarksFollowTextReloadedFromDisk() {
		TextView view = new(new TextBuffer("x\na\nb\nnew\nc"), "a.cs");
		DiffEditor diff = new(view, "a\nb\nc", "a.cs", false);
		diff.Sync(10f, 1f);

		view.Buffer.ReplaceChanged(view.Buffer.GetText(), "a\nb\nnew\nc");
		diff.Sync(10f, 1f);

		Assert.Equal("new", view.Snapshot.GetText(view.Snapshot.GetLineSpan(2)));
		Assert.True(view.DiffMarks!.Contains(2));
		Assert.False(view.DiffMarks.Contains(1));
	}

	private static int HiddenLines(TextView view) => view.Zones.Where(static zone => zone.Kind == TextView.ZoneKind.Hidden).Sum(static zone => zone.Hide);

	[Fact]
	public void RevertingAHunkKeepsItsLinesVisible() {
		string original = string.Join('\n', Enumerable.Range(0, 60).Select(static i => $"line {i}"));
		string modified = original.Replace("line 30", "changed 30");
		TextView view = new(new TextBuffer(modified), "a.cs");
		DiffEditor diff = new(view, original, "a.cs", false) { HideUnchangedRegions = true };
		diff.Sync(10f, 1f);

		int hiddenBefore = HiddenLines(view);

		view.Buffer.ReplaceChanged(view.Buffer.GetText(), original);
		diff.Sync(10f, 1f);

		Assert.Equal(hiddenBefore, HiddenLines(view));
		Assert.False(view.IsHiddenLine(30));
	}

	[Fact]
	public void WholeFileReplaceKeepsTheCaretOnItsLine() {
		TextView view = new(new TextBuffer("one\r\ntwo\r\nthree\r\nfour"));
		view.RestoreViewState(11, 11, 0f);
		view.Buffer.ReplaceChanged(view.Buffer.GetText(), "one\ntwo\nthree\nfour");

		Assert.Equal(2, view.CaretLine);
		Assert.Equal(1, view.CaretColumn);
	}
}
