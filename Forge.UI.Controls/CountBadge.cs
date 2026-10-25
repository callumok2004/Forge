using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class CountBadge : Panel
{
	public CountBadge() => MouseInputEnabled = false;

	public int Count {
		get;
		set {
			if (field == value) return;

			field = value;
			Visible = value > 0;
			Parent?.InvalidateLayout();
			Repaint();
		}
	}

	public float PreferredWidth => Ui is null ? 0f : Badge.Width(Ui.GetFonts().GetUiSmall(), Count, Scale);

	public override void Paint(IPainter painter) => Badge.Draw(painter, Count, ScreenX, ScreenY + (Tall / 2f), Scale, Theme.BadgeBackground, Theme.BadgeForeground);
}
