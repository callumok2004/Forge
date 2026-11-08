using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Common.Workspace;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class Sidebar : Panel, IViewPart
{
	public const string ExplorerId = ViewContainer.ExplorerId;
	public const string SourceControlId = "workbench.view.scm";
	public const string RunId = "workbench.view.debug";
	public const string SearchId = "workbench.view.search";

	private const float HeaderActionSize = 22f;
	private const float HeaderPaddingRight = 8f;

	private readonly Panel _header;
	private readonly Label _title;
	private readonly ViewPartHost _host;

	public Sidebar() {
		Name = "Sidebar";
		MouseInputEnabled = false;

		_header = new Panel { Name = "SidebarHeader", Dock = DockType.Top, MouseInputEnabled = false };
		_title = new Label { Name = "SidebarTitle", Dock = DockType.Fill, Font = FontRole.UiStrong, Foreground = Theme.SidebarTitleForeground, Padding = new Thickness(Metrics.Space8 + Metrics.Space4, 0f, 0f, 0f) };
		_header.AddChild(_title);
		AddChild(_header);

		_host = new ViewPartHost(this, _header, Sync);
	}

	public ExplorerView Explorer { get; } = new();

	public SourceControlView SourceControl { get; } = new();

	public SearchView Search { get; } = new();

	public DebugView Debug { get; } = new();

	public float Width {
		get;
		set {
			field = value;
			Parent?.InvalidateLayout();
		}
	} = Metrics.SidebarDefaultWidth;

	public string? SelectedPath {
		get => Explorer.SelectedPath;
		set => Explorer.SelectedPath = value;
	}

	public IGitRepository? Repository {
		get;
		set {
			field = value;
			Explorer.Repository = value;
			SourceControl.Repository = value;
			Repaint();
		}
	}

	public string? ActiveContainer => _host.Active?.Container.Id;

	public IReadOnlyList<ViewPaneContainer> Containers => _host.Containers;

	public void Attach(ViewPaneContainer container) => _host.Attach(container);

	public void Detach(ViewPaneContainer container) => _host.Detach(container);

	public void ShowContainer(string? id) => _host.Show(id);

	public void SetTree(IFileTree tree) => Explorer.SetTree(tree);

	public void CaptureExpanded(List<string> destination) => Explorer.CaptureExpanded(destination);

	public void RestoreExpanded(IReadOnlyList<string> paths) => Explorer.RestoreExpanded(paths);

	public void Resize(float width) => Width = Math.Clamp(width, Metrics.SidebarMinWidth, Metrics.SidebarMaxWidth);

	private void Sync() {
		_title.SetText(_host.Active?.Container.DisplayTitle ?? string.Empty);
		Repaint();
	}

	public override void PerformLayout() {
		float scale = Scale;
		float height = Metrics.SidebarHeaderHeight * scale;

		_header.SetSize(Wide, height);
		_header.DockPadding = new Thickness(0f, 0f, HeaderPaddingRight * scale, 0f);
		_host.LayoutActions(HeaderActionSize * scale, height);

		base.PerformLayout();
	}

	public override void PaintBackground(IPainter painter) => painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Theme.SidebarBackground);

	public override void PaintBorder(IPainter painter) => painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Metrics.StrokeThickness * Scale, Theme.PanelBorder);
}

public interface IViewPart
{
	string? ActiveContainer { get; }

	void Attach(ViewPaneContainer container);

	void Detach(ViewPaneContainer container);

	void ShowContainer(string? id);
}

public sealed class ViewPartHost(Panel part, Panel? header, Action changed)
{
	private readonly List<ViewPaneContainer> _containers = [];
	private readonly List<Panel> _actions = [];

	public ViewPaneContainer? Active { get; private set; }

	public IReadOnlyList<ViewPaneContainer> Containers => _containers;

	public void Attach(ViewPaneContainer container) {
		if (_containers.Contains(container)) return;

		_containers.Add(container);
		container.Visible = false;
		container.TitleActionsChanged += OnTitleActionsChanged;
		part.AddChild(container);
	}

	public void Detach(ViewPaneContainer container) {
		if (!_containers.Remove(container)) return;

		container.TitleActionsChanged -= OnTitleActionsChanged;
		part.RemoveChild(container);

		if (ReferenceEquals(Active, container)) {
			Active = null;
			SyncActions();
			changed();
		}
	}

	public void Show(string? id) {
		ViewPaneContainer? next = id is null ? null : _containers.Find(container => container.Container.Id == id);

		if (ReferenceEquals(next, Active)) return;

		Active = next;

		foreach (ViewPaneContainer container in _containers) container.Visible = ReferenceEquals(container, next);

		SyncActions();
		part.InvalidateLayout();
		changed();
	}

	private void OnTitleActionsChanged() {
		SyncActions();
		part.InvalidateLayout();
	}

	private void SyncActions() {
		if (header is null) return;

		foreach (Panel action in _actions) {
			if (ReferenceEquals(action.Parent, header)) header.RemoveChild(action);
		}

		_actions.Clear();

		if (Active is null) return;

		for (int i = Active.TitleActions.Count - 1; i >= 0; i--) {
			Panel action = Active.TitleActions[i];
			action.Dock = DockType.Right;
			header.AddChild(action);
			_actions.Add(action);
		}

		header.InvalidateLayout();
	}

	public void LayoutActions(float size, float headerHeight) {
		Active?.SyncActions();

		foreach (Panel action in _actions) {
			action.SetSize(size, size);
			action.DockMargin = new Thickness(0f, (headerHeight - size) / 2f, 0f, (headerHeight - size) / 2f);
		}
	}
}
