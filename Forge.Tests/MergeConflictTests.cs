using Forge.Extensions.MergeConflict;

namespace Forge.Tests;

public class MergeConflictTests
{
	private const string Sample = "one\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> branch\ntwo";

	[Fact]
	public void FindsBothSides() {
		List<MergeConflict> conflicts = MergeConflictScanner.Find(Sample);

		Assert.Single(conflicts);
		Assert.Equal("HEAD", conflicts[0].CurrentLabel);
		Assert.Equal("branch", conflicts[0].IncomingLabel);
	}

	[Fact]
	public void AcceptingCurrentKeepsOnlyMine() {
		string result = MergeConflictScanner.ResolveAll(Sample, MergeResolution.Current);

		Assert.Contains("mine", result, StringComparison.Ordinal);
		Assert.DoesNotContain("theirs", result, StringComparison.Ordinal);
		Assert.DoesNotContain("<<<", result, StringComparison.Ordinal);
	}

	[Fact]
	public void AcceptingIncomingKeepsOnlyTheirs() {
		string result = MergeConflictScanner.ResolveAll(Sample, MergeResolution.Incoming);

		Assert.Contains("theirs", result, StringComparison.Ordinal);
		Assert.DoesNotContain("mine", result, StringComparison.Ordinal);
	}

	[Fact]
	public void AcceptingBothKeepsEverythingWithoutMarkers() {
		string result = MergeConflictScanner.ResolveAll(Sample, MergeResolution.Both);

		Assert.Contains("mine", result, StringComparison.Ordinal);
		Assert.Contains("theirs", result, StringComparison.Ordinal);
		Assert.DoesNotContain("=======", result, StringComparison.Ordinal);
	}

	[Fact]
	public void ResolvesEveryConflictInTheFile() {
		string doubled = Sample + "\n" + Sample;

		Assert.Equal(2, MergeConflictScanner.Find(doubled).Count);
		Assert.Empty(MergeConflictScanner.Find(MergeConflictScanner.ResolveAll(doubled, MergeResolution.Current)));
	}
}
