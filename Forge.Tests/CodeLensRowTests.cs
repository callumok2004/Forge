using Forge.Editor;
using Forge.Text;

namespace Forge.Tests;

public class CodeLensRowTests
{
	private static TextView.LensLine Line(int line, int index) => new(line, [new TextView.LensSegment("1 reference", true, index)]);

	[Fact]
	public void AddingLinesWhileKeepingExistingOnes() {
		TextView view = new(new TextBuffer("a\nb\nc\nd\ne"));

		view.SetCodeLenses([Line(1, 0)], 12f);
		view.SetCodeLenses([Line(1, 0), Line(2, 1), Line(4, 2)], 12f);

		Assert.Equal(3, view.Zones.Count);
		Assert.Equal([0, 1, 3], view.Zones.Select(zone => zone.AfterLine));
	}

	[Fact]
	public void ReusesZoneForSameLineAndDropsMissing() {
		TextView view = new(new TextBuffer("a\nb\nc\nd"));

		view.SetCodeLenses([Line(0, 0), Line(2, 1)], 12f);
		int kept = view.Zones.Single(zone => zone.AfterLine == 1).Id;

		view.SetCodeLenses([Line(2, 5)], 14f);

		Assert.Single(view.Zones);
		Assert.Equal(kept, view.Zones[0].Id);
		Assert.Equal(14f, view.Zones[0].Height);
	}

	[Fact]
	public void LensesAboveViewportKeepFirstVisibleLineInPlace() {
		TextView view = new(new TextBuffer(string.Join('\n', Enumerable.Range(0, 200).Select(line => $"line {line}"))));
		view.RestoreViewState(0, 0, 1000f);

		float before = view.ScrollY - view.TopOf(100, view.LastLineHeight);

		view.SetCodeLenses([Line(10, 0), Line(50, 1), Line(90, 2)], 12f);

		Assert.Equal(before, view.ScrollY - view.TopOf(100, view.LastLineHeight));
	}

	[Fact]
	public void LensOnFirstLineSitsAboveIt() {
		TextView view = new(new TextBuffer("a\nb"));

		view.SetCodeLenses([Line(0, 0)], 12f);

		Assert.Equal(12f, view.TopOf(0, 10f));
		Assert.Equal(0f, view.ZoneTop(view.Zones[0].Id, 10f));
	}
}
