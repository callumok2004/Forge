using Forge.Editor;
using Forge.Text;

namespace Forge.Tests;

public class ViewZoneTests
{
	private const float LineHeight = 10f;

	[Fact]
	public void ZonePushesLaterLinesDown() {
		TextView view = new(new TextBuffer("a\nb\nc\nd"));

		view.AddZone(1, 25f);

		Assert.Equal(10f, view.TopOf(1, LineHeight));
		Assert.Equal(45f, view.TopOf(2, LineHeight));
		Assert.Equal(20f, view.ZoneTop(view.Zones[0].Id, LineHeight));
		Assert.Equal(65f, view.ContentHeight(LineHeight));
	}

	[Fact]
	public void PointsInsideZoneMapToLineAbove() {
		TextView view = new(new TextBuffer("a\nb\nc\nd"));

		view.AddZone(1, 25f);

		Assert.Equal(1, view.LineAtY(30f, LineHeight));
		Assert.Equal(2, view.LineAtY(46f, LineHeight));
		Assert.Equal(3, view.LineAtY(58f, LineHeight));
	}

	[Fact]
	public void InsertingLinesAboveMovesZone() {
		TextView view = new(new TextBuffer("a\nb\nc\nd"));

		view.AddZone(2, 25f);
		view.MoveTo(0, extend: false);
		view.InsertText("x\ny\n");

		Assert.Equal(4, view.Zones[0].AfterLine);
	}
}
