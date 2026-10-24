using Forge.Common.Render;
using Forge.Theming;

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
	private const float ActionHeight = 34f;
	private const float ActionGap = 4f;
	private const float IndicatorRadius = 9f;
	private const float IconSize = 18f;
	private const float BottomActionHeight = 28f;
	private const float BottomIconSize = 16f;
	private const float BottomPadding = 6f;

	private static readonly ActivityView[] Views =
	[
		ActivityView.Explorer,
		ActivityView.Search,
		ActivityView.SourceControl,
		ActivityView.Run,
		ActivityView.Extensions,
	];

	private readonly record struct Control(string CommandId, Func<ActivityBar, Codicon> Icon);

	private static readonly Control[] Controls =
	[
		new("workbench.action.toggleSidebarVisibility", static bar => bar.SidebarVisible ? Codicon.LayoutSidebarLeft : Codicon.LayoutSidebarLeftOff),
		new("workbench.action.togglePanel", static bar => bar.PanelVisible ? Codicon.LayoutPanel : Codicon.LayoutPanelOff),
		new("workbench.action.toggleAuxiliaryBar", static bar => bar.AuxiliaryBarVisible ? Codicon.LayoutSidebarRight : Codicon.LayoutSidebarRightOff),
		new("workbench.action.openSettings", static _ => Codicon.SettingsGear),
	];

	private int _hoverIndex = -1;

	public ActivityView Active { get; set; } = ActivityView.Explorer;

	public int SourceControlCount { get; set; }

	public bool SidebarVisible { get; set; } = true;

	public bool PanelVisible { get; set; }

	public bool AuxiliaryBarVisible { get; set; }

	public IVectorIcon? ExplorerIcon { get; set; }

	public static float Width(float scale) => (Metrics.CardMargin + Metrics.ActivityBarWidth) * scale;

	public static float CentreX(float scale) => (Metrics.CardMargin + (Metrics.ActivityBarWidth / 2f)) * scale;

	public int IndexAt(float x, float y, float top, float height, float scale) {
		float left = Metrics.CardMargin * scale;

		if (x < left || x > Width(scale) || y < top || y > top + height) return -1;

		float bottom = top + height - (BottomPadding * scale);
		float row = BottomActionHeight * scale;
		float controlsTop = bottom - (row * Controls.Length);

		if (y >= controlsTop) {
			int control = (int)((y - controlsTop) / row);
			return control >= 0 && control < Controls.Length ? Views.Length + control : -1;
		}

		float step = (ActionHeight + ActionGap) * scale;
		int index = (int)((y - top) / step);

		return index >= 0 && index < Views.Length && y - top - (index * step) < ActionHeight * scale ? index : -1;
	}

	public static string? CommandAt(int index) => index >= Views.Length && index < Views.Length + Controls.Length ? Controls[index - Views.Length].CommandId : null;

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
		float left = x + (Metrics.CardMargin * scale);
		float width = Metrics.ActivityBarWidth * scale;
		float centreX = left + (width / 2f);
		float item = ActionHeight * scale;
		float step = (ActionHeight + ActionGap) * scale;

		for (int i = 0; i < Views.Length; i++) {
			float top = y + (i * step);
			bool isActive = Views[i] == Active;
			bool isHovered = i == _hoverIndex;

			if (isActive) painter.FillRoundedRectangle(centreX - (item / 2f), top, item, item, IndicatorRadius * scale, Theme.ActivityBarActiveBackground);

			Color color = isActive ? Theme.ActivityBarActiveForeground : isHovered ? Theme.Foreground : Theme.ActivityBarForeground;

			DrawView(painter, Views[i], centreX, top + (item / 2f), IconSize * scale, color);

			if (Views[i] == ActivityView.SourceControl && SourceControlCount > 0) DrawBadge(painter, SourceControlCount, centreX + (item / 2f), top + item, scale);
		}

		float row = BottomActionHeight * scale;
		float controlsTop = y + height - (BottomPadding * scale) - (row * Controls.Length);

		for (int i = 0; i < Controls.Length; i++) {
			bool hovered = _hoverIndex == Views.Length + i;
			Color color = hovered ? Theme.ActivityBarActiveForeground : Theme.ActivityBarForeground;

			painter.DrawIcon(Controls[i].Icon(this), centreX, controlsTop + (i * row) + (row / 2f), BottomIconSize * scale, color);
		}
	}

	private void DrawBadge(IPainter painter, int count, float right, float bottom, float scale) {
		ITextFont font = painter.GetUiSmall();
		Span<char> text = stackalloc char[8];
		count.TryFormat(text, out int length);

		float height = 14f * scale;
		float width = Math.Max(font.Measure(text[..length]) + (6f * scale), height);
		float badgeRight = right - (2f * scale);
		float badgeBottom = bottom - scale;

		painter.FillRoundedRectangle(badgeRight - width, badgeBottom - height, width, height, height / 2f, Theme.ActivityBarBadgeBackground);

		float baseline = badgeBottom - height + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		painter.DrawText(font, text[..length], badgeRight - width + ((width - font.Measure(text[..length])) / 2f), baseline, Theme.ActivityBarBadgeForeground);
	}

	private void DrawView(IPainter painter, ActivityView view, float centreX, float centreY, float size, Color color) {
		if (view == ActivityView.Explorer && ExplorerIcon is { } folder) {
			painter.DrawVectorIcon(folder, centreX, centreY, size, color);
			return;
		}

		Codicon icon = view switch {
			ActivityView.Explorer => Codicon.Files,
			ActivityView.Search => Codicon.Search,
			ActivityView.SourceControl => Codicon.SourceControl,
			ActivityView.Run => Codicon.DebugAlt,
			_ => Codicon.Extensions,
		};

		painter.DrawIcon(icon, centreX, centreY, size, color);
	}
}
