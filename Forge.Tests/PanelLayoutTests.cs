using Forge.Common.Render;
using Forge.Common.UI;
using Forge.UI.Framework;
using Forge.Workbench;

namespace Forge.Tests;

public class PanelLayoutTests(ITestOutputHelper output)
{
	private sealed class FakeFont(float size) : ITextFont
	{
		public string GetFamilyName() => "Fake";
		public float GetSizeInPixels() => size;
		public float GetCellWidth() => size * 0.6f;
		public float GetLineHeight() => size * 1.3f;
		public float GetBaseline() => size;
		public float GetAscent() => size;
		public float GetDescent() => size * 0.3f;
		public bool IsMonospace() => true;
		public ushort GetGlyphIndex(char character) => 1;
		public float GetAdvance(char character) => size * 0.6f;
		public float Measure(ReadOnlySpan<char> text) => text.Length * size * 0.6f;
	}

	private sealed class FakeFonts : IFontSet
	{
		private static readonly FakeFont Font = new(13f);
		public ITextFont GetCode() => Font;
		public ITextFont GetCodeStrong() => Font;
		public ITextFont GetCodeSmall() => Font;
		public ITextFont GetCodeLens() => Font;
		public ITextFont GetUi() => Font;
		public ITextFont GetUiStrong() => Font;
		public ITextFont GetUiSmall() => Font;
		public ITextFont GetUiDescription() => Font;
		public ITextFont GetUi(float size, bool strong = false) => new FakeFont(size);
		public ITextFont GetMono(float size, bool strong = false) => new FakeFont(size);
		public ITextFont GetFamily(string[] families, float size) => new FakeFont(size);
		public float GetScale() => 1f;
		public bool Rescale(float scale) => false;
	}

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
