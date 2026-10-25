using Forge.Common.Commands;
using Forge.Text;
using Forge.Workbench;

namespace Forge.Tests;

public class PeekReferencesTests
{
	[Fact]
	public void PreviewKeepsUpToAWordBeforeAndTrims() {
		(string value, int start, int length) = PeekReferencesWidget.PreviewOf("        int total = Compute(values);   ", 21, 28);

		Assert.Equal("total = Compute(values);", value);
		Assert.Equal("Compute", value.Substring(start, length));
	}

	[Fact]
	public void PreviewAtLineStart() {
		(string value, int start, int length) = PeekReferencesWidget.PreviewOf("Foo.Bar();", 1, 4);

		Assert.Equal("Foo.Bar();", value);
		Assert.Equal(0, start);
		Assert.Equal(3, length);
	}

	[Fact]
	public void GroupsByFileSortedAndSelectsNearest() {
		PeekReferencesWidget widget = new();
		HostLocation[] locations = [
			new(@"C:\b.cs", 3, 5, 3, 8, string.Empty),
			new(@"C:\a.cs", 9, 1, 9, 4, string.Empty),
			new(@"C:\a.cs", 2, 1, 2, 4, string.Empty),
		];

		widget.Show("References", 9, 2, locations, static path => new TextBuffer(string.Join('\n', Enumerable.Range(1, 12).Select(line => $"line{line} abc"))));
		widget.SelectNearest(@"C:\a.cs", 9, 2);

		Assert.Equal([@"C:\a.cs", @"C:\b.cs"], widget.Groups.Select(group => group.Path));
		Assert.Equal([2, 9], widget.Groups[0].References.Select(reference => reference.Location.Line));
		Assert.Equal(9, widget.Selected?.Location.Line);
		Assert.True(widget.Groups[0].Expanded);
		Assert.False(widget.Groups[1].Expanded);
	}

	[Fact]
	public void ZoneHeightCapsToEightyPercentOfEditor() {
		PeekReferencesWidget widget = new() { HeightInLines = 40 };

		Assert.Equal(24f * 10f, widget.ZoneHeight(10f, 300f));
		Assert.Equal(18f * 10f, new PeekReferencesWidget().ZoneHeight(10f, 1000f));
	}
}
