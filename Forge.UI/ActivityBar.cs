using Forge.Common.Render;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.UI;

public enum ActivityView
{
	Explorer,
	Search,
	SourceControl,
	Run,
	Extensions,
}

public sealed class ActivityBar
{
	private static readonly ActivityView[] Views =
	[
		ActivityView.Explorer,
		ActivityView.Search,
		ActivityView.SourceControl,
		ActivityView.Run,
		ActivityView.Extensions,
	];

	private int _hoverIndex = -1;

	public ActivityView Active { get; set; } = ActivityView.Explorer;

	public int SourceControlCount { get; set; }

	public static float Width(float scale) => (Metrics.CardMargin + Metrics.ActivityBarWidth) * scale;

	private static float ItemHeight(float scale) => Metrics.ActivityBarWidth * scale;

	private static float ItemGap(float scale) => Metrics.CardMargin * scale;

	public int IndexAt(float x, float y, float top, float height, float scale) {
		float left = Metrics.CardMargin * scale;
		float bottom = top + height - (Metrics.CardMargin * scale);

		if (x < left || x > Width(scale) || y < top || y > bottom) return -1;

		float item = ItemHeight(scale);

		if (y >= bottom - item) return Views.Length;

		int index = (int)((y - top) / (item + ItemGap(scale)));
		return index >= 0 && index < Views.Length ? index : -1;
	}

	public bool SetHover(int index) {
		bool changed = _hoverIndex != index;
		_hoverIndex = index;
		return changed;
	}

	public bool Activate(int index) {
		if (index < 0 || index >= Views.Length) return false;

		if (Active == Views[index]) return false;

		Active = Views[index];
		return true;
	}

	public void Render(IPainter painter, float x, float y, float height, float scale) {
		float gutter = Metrics.CardMargin * scale;
		float item = ItemHeight(scale);
		float gap = ItemGap(scale);
		float iconSize = Metrics.ActivityIconSize * scale;

		x += gutter;
		height -= gutter;

		float width = Metrics.ActivityBarWidth * scale;
		float radius = Metrics.RadiusSmall * scale;

		for (int i = 0; i < Views.Length; i++) {
			float top = y + (i * (item + gap));
			bool isActive = Views[i] == Active;
			bool isHovered = i == _hoverIndex;

			if (isActive) {
				painter.FillRoundedRectangle(x, top, width, item, radius,
					Theme.ActivityBarActiveBackground);
			}
			else if (isHovered) painter.FillRoundedRectangle(x, top, width, item, radius, Theme.ListHoverBackground);

			Color color = isActive
				? Theme.ActivityBarActiveForeground
				: isHovered ? Theme.ActivityBarActiveForeground : Theme.ActivityBarForeground;

			DrawIcon(painter, Views[i], x + (width / 2f), top + (item / 2f), iconSize, color);

			if (Views[i] == ActivityView.SourceControl && SourceControlCount > 0) {
				float badgeWidth = Badge.Width(painter.GetUiSmall(), SourceControlCount, scale);

				Badge.Draw(painter, SourceControlCount,
					x + width - (2f * scale) - badgeWidth,
					top + (18f * scale) + (Badge.Height * scale / 2f), scale,
					Theme.Accent, Theme.TabActiveForeground);
			}
		}

		float settingsTop = y + height - item;
		bool settingsHovered = _hoverIndex == Views.Length;

		if (settingsHovered) {
			painter.FillRoundedRectangle(x, settingsTop, width, item, radius, Theme.ListHoverBackground);
		}

		painter.DrawIcon(
			Codicon.SettingsGear,
			x + (width / 2f),
			settingsTop + (item / 2f),
			iconSize,
			settingsHovered ? Theme.ActivityBarActiveForeground : Theme.ActivityBarForeground);
	}

	private static void DrawIcon(IPainter painter, ActivityView view, float centreX, float centreY, float size,
		Color color) {
		switch (view) {
			case ActivityView.Explorer:
				painter.DrawIcon(Codicon.Files, centreX, centreY, size, color);
				break;

			case ActivityView.Search:
				painter.DrawIcon(Codicon.Search, centreX, centreY, size, color);
				break;

			case ActivityView.SourceControl:
				painter.DrawIcon(Codicon.SourceControl, centreX, centreY, size, color);
				break;

			case ActivityView.Run:
				painter.DrawIcon(Codicon.DebugAlt, centreX, centreY, size, color);
				break;

			case ActivityView.Extensions:
				painter.DrawIcon(Codicon.Extensions, centreX, centreY, size, color);
				break;
		}
	}
}
