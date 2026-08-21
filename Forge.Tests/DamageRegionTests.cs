using Forge.Core;

namespace Forge.Tests;

public class DamageRegionTests
{
	[Fact]
	public void DistantBoxesAreKeptApartRatherThanBounded() {
		DamageRegion region = new();

		region.Add(new PixelRect(300, 400, 1200, 420));
		region.Add(new PixelRect(1400, 1030, 1600, 1050));

		Assert.Equal(2, region.Count);

		PixelRect bounds = region.Bounds();

		Assert.True(region.Area() * 4 < (long)bounds.Width * bounds.Height);
	}

	[Fact]
	public void ABoxAlreadyCoveredIsNotAddedAgain() {
		DamageRegion region = new();

		region.Add(new PixelRect(0, 0, 100, 100));
		region.Add(new PixelRect(10, 10, 20, 20));

		Assert.Equal(1, region.Count);
	}

	[Fact]
	public void AFullWindowBoxAbsorbsEverythingBeforeIt() {
		DamageRegion region = new();

		region.Add(new PixelRect(10, 10, 20, 20));
		region.Add(new PixelRect(900, 900, 950, 950));
		region.Add(new PixelRect(0, 0, 1000, 1000));

		Assert.Equal(1, region.Count);
		Assert.Equal(1000, region[0].Right);
	}

	[Fact]
	public void PastCapacityEverythingIsStillCovered() {
		DamageRegion region = new();

		PixelRect[] added =
		[
			new(0, 0, 10, 10),
			new(500, 0, 510, 10),
			new(0, 500, 10, 510),
			new(500, 500, 510, 510),
			new(250, 250, 260, 260),
			new(900, 900, 910, 910),
		];

		foreach (PixelRect rect in added) region.Add(rect);

		Assert.True(region.Count <= DamageRegion.Capacity);

		foreach (PixelRect rect in added) {
			bool covered = false;

			for (int i = 0; i < region.Count; i++) covered |= region[i].Contains(rect);

			Assert.True(covered, $"{rect.Left},{rect.Top} was dropped");
		}
	}

	[Fact]
	public void MergingPrefersThePairThatWastesLeast() {
		DamageRegion region = new();

		region.Add(new PixelRect(0, 0, 100, 100));
		region.Add(new PixelRect(900, 0, 1000, 100));
		region.Add(new PixelRect(0, 900, 100, 1000));
		region.Add(new PixelRect(900, 900, 1000, 1000));
		region.Add(new PixelRect(100, 0, 200, 100));

		Assert.Equal(DamageRegion.Capacity, region.Count);

		bool merged = false;

		for (int i = 0; i < region.Count; i++) {
			merged |= region[i].Left == 0 && region[i].Right == 200 && region[i].Bottom == 100;
		}

		Assert.True(merged);
	}

	[Fact]
	public void EmptyBoxesAreIgnored() {
		DamageRegion region = new();

		region.Add(PixelRect.Empty);
		region.Add(new PixelRect(10, 10, 10, 50));

		Assert.True(region.IsEmpty);
	}

	[Fact]
	public void ClearingLetsTheRegionBeReusedAcrossFrames() {
		DamageRegion region = new();

		region.Add(new PixelRect(0, 0, 50, 50));
		region.Clear();

		Assert.True(region.IsEmpty);
		Assert.Equal(0, region.Area());
	}
}
