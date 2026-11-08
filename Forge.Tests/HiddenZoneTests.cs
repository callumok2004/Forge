using Forge.Editor;
using Forge.Text;

namespace Forge.Tests;

public class HiddenZoneTests
{
	private static TextView Lines(int count) => new(new TextBuffer(string.Join('\n', Enumerable.Range(0, count))));

	[Fact]
	public void HiddenLinesCollapseIntoTheBar() {
		TextView view = Lines(10);
		view.AddZone(1, 24f, 5, TextView.ZoneKind.Hidden);

		Assert.Equal(44f, view.TopOf(7, 10f));
		Assert.Equal(74f, view.ContentHeight(10f));
		Assert.Equal(7, view.LineAtY(44f, 10f));
	}

	[Fact]
	public void OnlyLinesInsideTheRangeAreHidden() {
		TextView view = Lines(10);
		view.AddZone(1, 24f, 5, TextView.ZoneKind.Hidden);

		Assert.False(view.IsHiddenLine(1));
		Assert.True(view.IsHiddenLine(2));
		Assert.True(view.IsHiddenLine(6));
		Assert.False(view.IsHiddenLine(7));
	}

	[Fact]
	public void RemovingTheZoneRestoresLayout() {
		TextView view = Lines(10);
		int zone = view.AddZone(1, 24f, 5, TextView.ZoneKind.Hidden);
		view.RemoveZone(zone);

		Assert.False(view.IsHiddenLine(3));
		Assert.Equal(70f, view.TopOf(7, 10f));
	}
}
