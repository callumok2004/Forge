using Forge.Common.Render;
using Forge.Common.UI;
using Forge.UI.Framework;
using Forge.Workbench;

namespace Forge.Tests;

public class PanelLayoutTests(ITestOutputHelper output)
{
	[Fact]
	public void TabsStayVisibleAcrossViews() {
		typeof(ForgeDllMain).GetProperty(nameof(ForgeDllMain.Channels))!.SetValue(null, new Forge.Commands.OutputChannels());
		UiSystem ui = new(_ => { }, () => { }, () => 1f, () => KeyModifiers.None) { Fonts = new FakeFonts() };
		using PanelPart panel = new() { Dock = DockType.Fill };
		ui.Root.AddChild(panel);

		foreach (PanelView view in new[] { PanelView.Output, PanelView.DebugConsole, PanelView.Problems }) {
			panel.Show(view);
			ui.Layout(1000f, 300f);

			Panel title = panel.Children.First(child => child.Name == "PanelTitle");
			output.WriteLine($"{view}: title {title.X},{title.Y} {title.Wide}x{title.Tall}");

			foreach (Panel child in title.Children.Where(child => child.Visible)) output.WriteLine($"  {child.Name} {child.X},{child.Y} {child.Wide}x{child.Tall}");

			Assert.True(title.Tall > 0f);
			Assert.All(title.Children.OfType<PanelTab>().Where(tab => tab.Visible), tab => Assert.True(tab.Wide > 0f, $"{tab.Name} collapsed after showing {view}"));
		}
	}
}
