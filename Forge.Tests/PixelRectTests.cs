using Forge.Core;

namespace Forge.Tests;

public class PixelRectTests
{
	[Fact]
	public void AroundRoundsOutwardsSoNoPixelIsLost() {
		PixelRect rect = PixelRect.Around(10.4f, 20.9f, 5.2f, 3.3f);

		Assert.Equal(10, rect.Left);
		Assert.Equal(20, rect.Top);
		Assert.Equal(16, rect.Right);
		Assert.Equal(25, rect.Bottom);
	}

	[Fact]
	public void EmptyIsIdentityForUnion() {
		PixelRect rect = new(4, 8, 12, 16);

		Assert.Equal(rect.Left, rect.Union(PixelRect.Empty).Left);
		Assert.Equal(rect.Right, rect.Union(PixelRect.Empty).Right);

		PixelRect fromEmpty = PixelRect.Empty.Union(rect);

		Assert.Equal(rect.Top, fromEmpty.Top);
		Assert.Equal(rect.Bottom, fromEmpty.Bottom);
	}

	[Fact]
	public void UnionOfTwoFramesCoversBoth() {
		PixelRect previous = new(0, 0, 100, 20);
		PixelRect current = new(300, 400, 320, 420);

		PixelRect repaint = current.Union(previous);

		Assert.True(repaint.Contains(previous));
		Assert.True(repaint.Contains(current));
	}

	[Fact]
	public void DisjointRectanglesDoNotIntersect() {
		PixelRect damage = new(0, 0, 50, 50);

		Assert.False(damage.Intersects(50f, 0f, 10f, 10f));
		Assert.False(damage.Intersects(0f, 50f, 10f, 10f));
		Assert.True(damage.Intersects(49f, 49f, 10f, 10f));
	}

	[Fact]
	public void ZeroSizedBoxesNeverIntersect() {
		PixelRect damage = new(10, 10, 20, 20);

		Assert.False(damage.Intersects(15f, 15f, 0f, 10f));
		Assert.False(damage.Intersects(15f, 15f, 10f, 0f));
		Assert.False(PixelRect.Empty.Intersects(0f, 0f, 100f, 100f));
	}

	[Fact]
	public void IntersectClampsToTheSurface() {
		PixelRect surface = PixelRect.FromSize(800, 600);
		PixelRect clamped = new PixelRect(-40, -40, 900, 700).Intersect(surface);

		Assert.Equal(0, clamped.Left);
		Assert.Equal(0, clamped.Top);
		Assert.Equal(800, clamped.Right);
		Assert.Equal(600, clamped.Bottom);
	}

	[Fact]
	public void AnIntersectionThatMissesEntirelyReadsAsEmpty() {
		PixelRect result = new PixelRect(0, 0, 10, 10).Intersect(new PixelRect(50, 50, 60, 60));

		Assert.True(result.IsEmpty);
	}
}
