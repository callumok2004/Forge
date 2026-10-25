using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.Common.Editor;

namespace Forge.Workbench;

public sealed class TabStrip : Panel
{
	private readonly List<EditorTab> _tabs = [];

	public TabStrip() {
		Name = "TabStrip";
		Dock = DockType.Top;
		MouseInputEnabled = false;
	}

	public static float Height(float scale) => (Metrics.TabHeight + (Metrics.TabStripPadding * 2f)) * scale;

	public Action<int>? Activated { get; set; }

	public Action<int>? Closed { get; set; }

	public void Sync(IReadOnlyList<IDocument> documents, int activeIndex) {
		while (_tabs.Count < documents.Count) {
			EditorTab tab = new(this);
			_tabs.Add(tab);
			AddChild(tab);
		}

		while (_tabs.Count > documents.Count) {
			RemoveChild(_tabs[^1]);
			_tabs.RemoveAt(_tabs.Count - 1);
			InvalidateLayout();
		}

		for (int i = 0; i < documents.Count; i++) _tabs[i].Bind(documents[i], i, i == activeIndex);
	}

	public override void PerformLayout() {
		float scale = Scale;
		float padding = Metrics.TabStripPadding * scale;

		DockPadding = new Thickness(padding, padding, padding, padding);

		foreach (EditorTab tab in _tabs) {
			tab.DockMargin = new Thickness(0f, 0f, Metrics.TabGap * scale, 0f);
			tab.SetSize(tab.PreferredWidth(scale), Metrics.TabHeight * scale);
		}

		base.PerformLayout();
	}

	public override void PaintBackground(IPainter painter) => painter.PushClip(ScreenX, ScreenY, Wide, Tall);

	public override void PostChildPaint(IPainter painter) => painter.PopClip();
}
