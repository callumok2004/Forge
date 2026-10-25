using Forge.Common.Render;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class TabCloseButton : Button
{
	private const float DotSize = 4f;
	private const float GlyphSize = 14f;

	private readonly EditorTab _tab;

	public TabCloseButton(EditorTab tab) {
		_tab = tab;
		Name = "Close";
		HoverBackground = Theme.ListHoverBackground;
	}

	public bool Dirty {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public override void OnCursorEntered() {
		base.OnCursorEntered();
		_tab.Repaint();
	}

	public override void OnCursorExited() {
		base.OnCursorExited();
		_tab.Repaint();
	}

	public override void Paint(IPainter painter) {
		float centreX = ScreenX + (Wide / 2f);
		float centreY = ScreenY + (Tall / 2f);

		if (Dirty && !IsCursorOver) painter.FillCircle(centreX, centreY, DotSize * Scale, Theme.Accent);
		else if (_tab.Selected || _tab.Hovered) painter.DrawIcon(Codicon.Close, centreX, centreY, GlyphSize * Scale, _tab.Foreground);
	}
}
