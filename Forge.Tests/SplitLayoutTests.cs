using Forge.UI.Layout;

namespace Forge.Tests;

public class SplitLayoutTests
{
	private const float Header = 22f;
	private const float MinimumBody = 66f;

	private sealed class Pane
	{
		public bool Expanded = true;
		public float? ExpandedSize;
		public SplitLayout.Item Item = null!;

		public float Minimum => Header + (Expanded ? MinimumBody : 0f);

		public float Maximum => Expanded ? float.PositiveInfinity : Header;
	}

	private static Pane Add(SplitLayout layout, float size, int index) {
		Pane pane = new();
		pane.Item = layout.Add(size, () => pane.Minimum, () => pane.Maximum, index);
		return pane;
	}

	private static void SetExpanded(SplitLayout layout, Pane pane, bool expanded) {
		if (pane.Expanded && !expanded) pane.ExpandedSize = pane.Item.Size;

		pane.Expanded = expanded;
		layout.ViewChanged(pane.Item, expanded ? pane.ExpandedSize : null);
	}

	private static float[] Sizes(SplitLayout layout) => [.. layout.Items.Select(static item => item.Size)];

	[Fact]
	public void SinglePaneFillsTheContainer() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);

		Assert.Equal([600f], Sizes(layout));
	}

	[Fact]
	public void SecondPaneKeepsItsSizeAndTheFirstShrinks() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);

		Assert.Equal([380f, 220f], Sizes(layout));
	}

	[Fact]
	public void ThirdPaneTakesSpaceFromTheOthersInOrder() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);
		Add(layout, 220f, 2);

		Assert.Equal(600f, Sizes(layout).Sum());
		Assert.Equal(220f, Sizes(layout)[2]);
	}

	[Fact]
	public void CollapsingGivesSpaceToTheOtherPane() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Pane second = Add(layout, 220f, 1);

		SetExpanded(layout, second, false);

		Assert.Equal([578f, 22f], Sizes(layout));
	}

	[Fact]
	public void ReexpandingRestoresThePreviousSize() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Pane second = Add(layout, 220f, 1);

		SetExpanded(layout, second, false);
		SetExpanded(layout, second, true);

		Assert.Equal([380f, 220f], Sizes(layout));
	}

	[Fact]
	public void CollapsingTheFirstPaneGivesSpaceToTheSecond() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Pane first = Add(layout, 220f, 0);
		Add(layout, 220f, 1);

		SetExpanded(layout, first, false);

		Assert.Equal([22f, 578f], Sizes(layout));
	}

	[Fact]
	public void AllCollapsedLeavesEmptySpace() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Pane first = Add(layout, 220f, 0);
		Pane second = Add(layout, 220f, 1);

		SetExpanded(layout, first, false);
		SetExpanded(layout, second, false);

		Assert.Equal([22f, 22f], Sizes(layout));
	}

	[Fact]
	public void SashDragMovesSpaceBetweenNeighbours() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);

		layout.SashStart(0, 380f);
		layout.SashChange(300f);
		layout.SashEnd();

		Assert.Equal([300f, 300f], Sizes(layout));
	}

	[Fact]
	public void SashDragStopsAtMinimumBodySize() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);

		layout.SashStart(0, 380f);
		layout.SashChange(0f);
		layout.SashEnd();

		Assert.Equal([88f, 512f], Sizes(layout));
	}

	[Fact]
	public void SashDragIsRelativeToTheStartSizes() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);

		layout.SashStart(0, 380f);
		layout.SashChange(0f);
		layout.SashChange(400f);
		layout.SashEnd();

		Assert.Equal([400f, 200f], Sizes(layout));
	}

	[Fact]
	public void SashDragPushesThroughToFurtherPanes() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 200f, 0);
		Add(layout, 200f, 1);
		Add(layout, 200f, 2);
		layout.SashStart(0, 200f);
		layout.SashChange(500f);
		layout.SashEnd();

		Assert.Equal([424f, 88f, 88f], Sizes(layout));
	}

	[Fact]
	public void ContainerResizeKeepsProportions() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);
		layout.SashStart(0, 380f);
		layout.SashChange(300f);
		layout.SashEnd();

		layout.Layout(800f);

		Assert.Equal([400f, 400f], Sizes(layout));
	}

	[Fact]
	public void ContainerShrinkRespectsMinimums() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);

		layout.Layout(150f);

		Assert.All(Sizes(layout), static size => Assert.True(size >= 88f));
	}

	[Fact]
	public void CollapsedPaneStaysAtHeaderOnContainerResize() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Pane second = Add(layout, 220f, 1);
		SetExpanded(layout, second, false);

		layout.Layout(900f);

		Assert.Equal([878f, 22f], Sizes(layout));
	}

	[Fact]
	public void CannotDragASashNextToTheOnlyCollapsedNeighbour() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Pane second = Add(layout, 220f, 1);
		SetExpanded(layout, second, false);

		Assert.False(layout.CanResize(0));
	}

	[Fact]
	public void CanDragBetweenExpandedPanes() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);

		Assert.True(layout.CanResize(0));
		Assert.False(layout.CanResize(1));
		Assert.False(layout.CanResize(-1));
	}

	[Fact]
	public void RemovingAnExpandedPaneDistributes() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 200f, 0);
		Add(layout, 200f, 1);
		Add(layout, 200f, 2);

		layout.Remove(1, distribute: true);

		Assert.Equal([300f, 300f], Sizes(layout));
	}

	[Fact]
	public void RemovingTheLastPaneLeavesNothing() {
		SplitLayout layout = new();
		layout.Layout(600f);
		Add(layout, 200f, 0);

		layout.Remove(0, distribute: true);

		Assert.Empty(layout.Items);
	}

	[Fact]
	public void PanesAddedBeforeFirstLayoutFillOnLayout() {
		SplitLayout layout = new();
		Add(layout, 220f, 0);
		Add(layout, 220f, 1);

		layout.Layout(600f);

		Assert.Equal(600f, Sizes(layout).Sum());
		Assert.All(Sizes(layout), static size => Assert.True(size >= 88f));
	}

	[Fact]
	public void SizesAlwaysFillTheContainerWhenExpanded() {
		Random random = new(3);

		for (int round = 0; round < 200; round++) {
			SplitLayout layout = new();
			float container = random.Next(300, 1200);
			layout.Layout(container);
			List<Pane> panes = [];
			int count = random.Next(1, 5);

			for (int i = 0; i < count; i++) panes.Add(Add(layout, random.Next(60, 400), i));

			for (int step = 0; step < 10; step++) {
				switch (random.Next(3)) {
					case 0:
						Pane pane = panes[random.Next(panes.Count)];
						SetExpanded(layout, pane, !pane.Expanded);
						break;

					case 1 when panes.Count > 1:
						int sash = random.Next(panes.Count - 1);
						float start = Sizes(layout).Take(sash + 1).Sum();
						layout.SashStart(sash, start);
						layout.SashChange(start + random.Next(-300, 300));
						layout.SashEnd();
						break;

					default:
						container = random.Next(300, 1200);
						layout.Layout(container);
						break;
				}

				float minimum = panes.Sum(static pane => pane.Minimum);

				foreach (Pane pane in panes) Assert.True(pane.Item.Size >= pane.Minimum - 0.5f && pane.Item.Size <= pane.Maximum + 0.5f);

				if (panes.Exists(static pane => pane.Expanded) && container >= minimum) Assert.Equal(container, Sizes(layout).Sum(), 0.5f);
			}
		}
	}
}
