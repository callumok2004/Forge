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

	public const int SearchOrder = 10;
	public const int ExtensionsOrder = 40;

	private readonly List<Entry> _entries = [];
	private readonly ActionButton _search;
	private readonly ActionButton _extensions;
	private readonly ActionButton _toggleSidebar;
	private readonly ActionButton _togglePanel;
	private readonly ActionButton _toggleAuxiliaryBar;
	private readonly ActionButton _manage;

	private sealed record Entry(string Id, int Order, ActionButton Button);

	public ActivityBar() {
		Name = "ActivityBar";
		Dock = DockType.Left;
		MouseInputEnabled = false;

		_search = CreateBuiltIn(ActivityView.Search, Codicon.Search);
		_extensions = CreateBuiltIn(ActivityView.Extensions, Codicon.Extensions);
		_entries.Add(new Entry("workbench.view.search", SearchOrder, _search));
		_entries.Add(new Entry("workbench.view.extensions", ExtensionsOrder, _extensions));

		_manage = CreateControl("workbench.action.openSettings", Codicon.SettingsGear);
		_toggleAuxiliaryBar = CreateControl("workbench.action.toggleAuxiliaryBar", Codicon.LayoutSidebarRightOff);
		_togglePanel = CreateControl("workbench.action.togglePanel", Codicon.LayoutPanelOff);
		_toggleSidebar = CreateControl("workbench.action.toggleSidebarVisibility", Codicon.LayoutSidebarLeft);

		Reorder();
	}

	public Action<ActivityView>? ViewSelected { get; set; }

	public Action<string>? ContainerSelected { get; set; }

	public string? ActiveId {
		get;
		set {
			field = value;

			foreach (Entry entry in _entries) entry.Button.Checked = entry.Id == value;
		}
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

	public static float Width(float scale) => (Metrics.CardMargin + Metrics.ActivityBarWidth) * scale;

	public static float CentreX(float scale) => (Metrics.CardMargin + (Metrics.ActivityBarWidth / 2f)) * scale;

	public bool Contains(string id) => _entries.Exists(entry => entry.Id == id);

	public void AddContainer(ViewContainer container) {
		if (Contains(container.Id)) return;

		ContainerButton button = new(container.Id) {
			Name = container.Title,
			Icon = container.Icon,
			VectorIcon = container.VectorIcon,
			IconSize = IconSize,
			Dock = DockType.Top,
			Foreground = Theme.ActivityBarForeground,
			HoverForeground = Theme.Foreground,
			HoverBackground = Color.Transparent,
			CheckedRadius = IndicatorRadius,
			Movable = container.Movable,
		};

		string id = container.Id;
		button.Action = () => ContainerSelected?.Invoke(id);
		button.Checked = ActiveId == id;
		_entries.Add(new Entry(id, container.Order, button));
		AddChild(button);
		Reorder();
	}

	public void RemoveContainer(string id) {
		int index = _entries.FindIndex(entry => entry.Id == id);

		if (index < 0) return;

		RemoveChild(_entries[index].Button);
		_entries.RemoveAt(index);
		InvalidateLayout();
		Repaint();
	}

	public void SetBadge(string id, int count) {
		if (_entries.Find(entry => entry.Id == id) is { } entry) entry.Button.BadgeCount = count;
	}

	public void SetVectorIcon(string id, IVectorIcon? icon) {
		if (_entries.Find(entry => entry.Id == id) is { } entry) entry.Button.VectorIcon = icon;
	}

	private void Reorder() {
		_entries.Sort(static (left, right) => left.Order.CompareTo(right.Order));

		foreach (Entry entry in _entries) RemoveChild(entry.Button);

		foreach (Entry entry in _entries) AddChild(entry.Button);

		InvalidateLayout();
		Repaint();
	}

	private ActionButton CreateBuiltIn(ActivityView view, Codicon icon) {
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

		foreach (Entry entry in _entries) {
			entry.Button.SetSize(width, ActionHeight * scale);
			entry.Button.DockMargin = new Thickness(0f, 0f, 0f, ActionGap * scale);
		}

		foreach (ActionButton control in (ReadOnlySpan<ActionButton>)[_toggleSidebar, _togglePanel, _toggleAuxiliaryBar, _manage]) control.SetSize(width, BottomActionHeight * scale);

		base.PerformLayout();
	}
}
