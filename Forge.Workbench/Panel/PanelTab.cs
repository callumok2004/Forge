using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class PanelTab : Button
{
	private const float HighlightHeight = 22f;
	private const float UnderlineHeight = 2f;

	public PanelTab(string title) {
		Name = title;
		Dock = DockType.Left;
		Padding = new Thickness(Metrics.Space8, 0f, Metrics.Space8, 0f);
		Foreground = Theme.TabInactiveForeground;
		HoverBackground = Theme.ListHoverBackground;
		SizeToContents = true;
		SetText(title);
	}

	public bool Selected {
		get;
		set {
			if (field == value) return;

			field = value;
			Foreground = value ? Theme.TabActiveForeground : Theme.TabInactiveForeground;
			Repaint();
		}
	}

	public override void PaintBackground(IPainter painter) {
		if (Selected || !IsCursorOver) return;

		float scale = Scale;
		float height = HighlightHeight * scale;
		painter.FillRoundedRectangle(ScreenX, ScreenY + ((Tall - height) / 2f), Wide, height, Metrics.RadiusSmall * scale, HoverBackground);
	}

	public override void Paint(IPainter painter) {
		PaintContents(painter, Foreground);

		if (!Selected) return;

		float scale = Scale;
		float inset = Padding.Left * scale;
		painter.FillRectangle(ScreenX + inset, ScreenY + Tall - (UnderlineHeight * scale), Wide - (inset * 2f), UnderlineHeight * scale, Theme.TabActiveBorderTop);
	}
}
