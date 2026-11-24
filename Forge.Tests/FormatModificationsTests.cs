using Forge.Common.Commands;
using Forge.Text;
using Forge.Workspace;

namespace Forge.Tests;

public class FormatModificationsTests
{
	[Fact]
	public void UnchangedFileHasNoRanges() => Assert.Empty(ModifiedRanges.Compute("a\nb\n", "a\nb\n"));

	[Fact]
	public void SingleChangedLine() => Assert.Equal([new ModifiedRange(2, 2, 4)], ModifiedRanges.Compute("a\nb\nc", "a\nbXY\nc"));

	[Fact]
	public void InsertedLines() => Assert.Equal([new ModifiedRange(2, 3, 3)], ModifiedRanges.Compute("a\nd", "a\nb1\nc1\nd"));

	[Fact]
	public void DeletedLinesMarkThePreviousLine() => Assert.Equal([new ModifiedRange(1, 1, 2)], ModifiedRanges.Compute("a\nb\nc", "a\nc"));

	[Fact]
	public void DeletionAtTopMarksFirstLine() => Assert.Equal([new ModifiedRange(1, 1, 2)], ModifiedRanges.Compute("x\na\nb", "a\nb"));

	[Fact]
	public void SeparateChangesStaySeparate() => Assert.Equal([new ModifiedRange(1, 1, 3), new ModifiedRange(4, 4, 3)], ModifiedRanges.Compute("a\nb\nc\nd", "a1\nb\nc\nd1"));

	[Fact]
	public void AdjacentLineChangesAreOneHunk() => Assert.Equal([new ModifiedRange(2, 3, 3)], ModifiedRanges.Compute("a\nb\nc\nd", "a\nb1\nc1\nd"));

	[Fact]
	public void EndColumnExcludesCrLf() => Assert.Equal([new ModifiedRange(1, 1, 4)], ModifiedRanges.Compute("ab\r\nc", "abc\r\nc"));

	[Fact]
	public void ChangeOnLastLineWithoutNewline() => Assert.Equal([new ModifiedRange(2, 2, 6)], ModifiedRanges.Compute("a\nb", "a\nbbbbb"));

	[Fact]
	public void AppendedTrailingNewlineIsInRange() {
		List<ModifiedRange> ranges = ModifiedRanges.Compute("a", "a\n");

		Assert.Single(ranges);
		Assert.True(ranges[0].StartLine >= 1 && ranges[0].EndLine <= 2);
	}

	[Fact]
	public void EverythingReplaced() => Assert.Equal([new ModifiedRange(1, 2, 2)], ModifiedRanges.Compute("a\nb", "x\ny"));

	[Fact]
	public void EmptyOriginal() => Assert.Equal([new ModifiedRange(1, 2, 2)], ModifiedRanges.Compute("", "x\ny"));

	private static HostTextEdit Edit(int line, int start, int end, string text = " ") => new(line, start, line, end, text);

	[Fact]
	public void TouchingEditsIntersect() => Assert.True(RangeFormatting.Intersects(Edit(1, 1, 3), Edit(1, 3, 5)));

	[Fact]
	public void DisjointEditsDoNotIntersect() => Assert.False(RangeFormatting.Intersects(Edit(1, 1, 2), Edit(1, 3, 5)));

	[Fact]
	public void MultiLineEditIntersectsInnerLine() => Assert.True(RangeFormatting.Intersects(new HostTextEdit(1, 1, 3, 1, ""), Edit(2, 1, 2)));

	[Fact]
	public void EmptyListsNeverIntersect() => Assert.False(RangeFormatting.Intersecting([], [Edit(1, 1, 2)]));

	[Fact]
	public async Task EachRangeIsFormattedOnce() {
		List<ModifiedRange> requested = [];

		List<HostTextEdit> edits = await RangeFormatting.ComputeAsync([new ModifiedRange(1, 1, 5), new ModifiedRange(5, 6, 3)], range => {
			requested.Add(range);
			return Task.FromResult<IReadOnlyList<HostTextEdit>>([Edit(range.StartLine, 1, 1, "\t")]);
		});

		Assert.Equal([new ModifiedRange(1, 1, 5), new ModifiedRange(5, 6, 3)], requested);
		Assert.Equal(2, edits.Count);
	}

	[Fact]
	public async Task IntersectingEditsMergeRangesAndRecompute() {
		List<ModifiedRange> requested = [];

		List<HostTextEdit> edits = await RangeFormatting.ComputeAsync([new ModifiedRange(1, 1, 5), new ModifiedRange(3, 3, 5)], range => {
			requested.Add(range);

			IReadOnlyList<HostTextEdit> result = range.StartLine == 1 && range.EndLine == 3 ? [new HostTextEdit(1, 1, 3, 1, "merged")] : [new HostTextEdit(1, 1, 3, 1, "x")];
			return Task.FromResult(result);
		});

		Assert.Equal(3, requested.Count);
		Assert.Equal(new ModifiedRange(1, 3, 5), requested[2]);
		Assert.Equal("merged", Assert.Single(edits).NewText);
	}

	[Fact]
	public async Task NoRangesNoRequests() {
		int calls = 0;

		List<HostTextEdit> edits = await RangeFormatting.ComputeAsync([], _ => {
			calls++;
			return Task.FromResult<IReadOnlyList<HostTextEdit>>([]);
		});

		Assert.Empty(edits);
		Assert.Equal(0, calls);
	}
}
