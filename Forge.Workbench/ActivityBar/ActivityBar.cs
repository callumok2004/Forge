using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public enum ActivityView
{
	Explorer,
	Search,
	SourceControl,
	Run,
	Extensions,
}

public sealed class ActivityBar : Panel
{
	private const float ActionHeight = 34f;
	private const float ActionGap = 4f;
	private const float IndicatorRadius = 9f;
	private const float IconSize = 18f;
	private const float BottomActionHeight = 28f;
	private const float BottomIconSize = 16f;
	private const float BottomPadding = 6f;

	private readonly ActionButton[] _views;
	private readonly ActionButton _toggleSidebar;
	private readonly ActionButton _togglePanel;
	private readonly ActionButton _toggleAuxiliaryBar;
	private readonly ActionButton _manage;

	public ActivityBar() {
		Name = "ActivityBar";
		Dock = DockType.Left;
		MouseInputEnabled = false;

		_views = [
			CreateView(ActivityView.Explorer, Codicon.Files),
			CreateView(ActivityView.Search, Codicon.Search),
			CreateView(ActivityView.SourceControl, Codicon.SourceControl),
			CreateView(ActivityView.Run, Codicon.DebugAlt),
			CreateView(ActivityView.Extensions, Codicon.Extensions),
		];

		_manage = CreateControl("workbench.action.openSettings", Codicon.SettingsGear);
		_toggleAuxiliaryBar = CreateControl("workbench.action.toggleAuxiliaryBar", Codicon.LayoutSidebarRightOff);
		_togglePanel = CreateControl("workbench.action.togglePanel", Codicon.LayoutPanelOff);
		_toggleSidebar = CreateControl("workbench.action.toggleSidebarVisibility", Codicon.LayoutSidebarLeft);

		_views[0].Checked = true;
	}

	public Action<ActivityView>? ViewSelected { get; set; }

	public ActivityView Active {
		get;
		set {
			field = value;

			for (int i = 0; i < _views.Length; i++) _views[i].Checked = i == (int)value;
		}
	}

	public int SourceControlCount {
		get => _views[(int)ActivityView.SourceControl].BadgeCount;
		set => _views[(int)ActivityView.SourceControl].BadgeCount = value;
	}

	public bool SidebarVisible {
		set => _toggleSidebar.Icon = value ? Codicon.LayoutSidebarLeft : Codicon.LayoutSidebarLeftOff;
	}

	public bool PanelVisible {
		set => _togglePanel.Icon = value ? Codicon.LayoutPanel : Codicon.LayoutPanelOff;
	}

	public bool AuxiliaryBarVisible {
		set => _toggleAuxiliaryBar.Icon = value ? Codicon.LayoutSidebarRight : Codicon.LayoutSidebarRightOff;
	}

	public IVectorIcon? ExplorerIcon {
		set => _views[(int)ActivityView.Explorer].VectorIcon = value;
	}

	public static float Width(float scale) => (Metrics.CardMargin + Metrics.ActivityBarWidth) * scale;

	public static float CentreX(float scale) => (Metrics.CardMargin + (Metrics.ActivityBarWidth / 2f)) * scale;

	private ActionButton CreateView(ActivityView view, Codicon icon) {
		ActionButton button = new() {
			Name = view.ToString(),
			Icon = icon,
			IconSize = IconSize,
			Dock = DockType.Top,
			Foreground = Theme.ActivityBarForeground,
			HoverForeground = Theme.Foreground,
			HoverBackground = Color.Transparent,
			CheckedRadius = IndicatorRadius,
			Action = () => ViewSelected?.Invoke(view),
		};

		AddChild(button);
		return button;
	}

	private ActionButton CreateControl(string command, Codicon icon) {
		ActionButton button = new() {
			Name = command,
			Command = command,
			Icon = icon,
			IconSize = BottomIconSize,
			Dock = DockType.Bottom,
			Foreground = Theme.ActivityBarForeground,
			HoverForeground = Theme.ActivityBarActiveForeground,
			HoverBackground = Color.Transparent,
		};

		AddChild(button);
		return button;
	}

	public override void PerformLayout() {
		float scale = Scale;
		float margin = Metrics.CardMargin * scale;
		float width = Metrics.ActivityBarWidth * scale;

		SetSize(Width(scale), Tall);
		DockPadding = new Thickness(margin, 0f, 0f, BottomPadding * scale);

		foreach (ActionButton view in _views) {
			view.SetSize(width, ActionHeight * scale);
			view.DockMargin = new Thickness(0f, 0f, 0f, ActionGap * scale);
		}

		foreach (ActionButton control in (ReadOnlySpan<ActionButton>)[_toggleSidebar, _togglePanel, _toggleAuxiliaryBar, _manage]) control.SetSize(width, BottomActionHeight * scale);

		base.PerformLayout();
	}
}
