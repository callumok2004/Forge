using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core;
using Forge.Theming;
using Forge.Workbench;

namespace Forge.Shell;

public sealed class WorkbenchViews : IWorkbenchViews, IViewDragTarget
{
	private readonly Workbench _workbench;
	private readonly Action<Action> _post;
	private readonly Dictionary<string, Entry> _containers = [with(StringComparer.Ordinal)];
	private readonly Dictionary<string, EditorPane> _editors = [with(StringComparer.Ordinal)];
	private readonly List<ViewDescriptor> _pending = [];
	private readonly Lock _gate = new();
	private readonly DropOverlay _overlay = new();

	private string? _dragging;

	private sealed class Entry(ViewContainer container, ViewPaneContainer host, ViewLocation location)
	{
		public ViewContainer Container { get; } = container;

		public ViewPaneContainer Host { get; } = host;

		public ViewLocation Location { get; set; } = location;
	}

	public WorkbenchViews(Workbench workbench, Action<Action> post) {
		_workbench = workbench;
		_post = post;
		ViewDrag.Target = this;
	}

	public event Action? Changed;

	public Action Refresh { get; set; } = static () => { };

	public Func<(float Width, float Height)> WindowSize { get; set; } = static () => (0f, 0f);

	public Dictionary<string, string> SavedLocations { get; set; } = [];

	public Panel? OverlayParent { get; set; }

	public Action<string>? ContainerShown { get; set; }

	private IViewPart Part(ViewLocation location) => location switch {
		ViewLocation.Sidebar => _workbench.Sidebar,
		ViewLocation.Auxiliary => _workbench.Auxiliary,
		_ => _workbench.Panel,
	};

	public void RegisterContainer(ViewContainer container) => _post(() => Register(container));

	public void Register(ViewContainer container) {
		if (_containers.ContainsKey(container.Id)) return;

		ViewLocation location = SavedLocations.TryGetValue(container.Id, out string? saved) && Enum.TryParse(saved, out ViewLocation parsed) && container.Movable ? parsed : container.Location;
		Entry entry = new(container, new ViewPaneContainer(container), location);
		_containers[container.Id] = entry;
		Place(entry);

		for (int i = _pending.Count - 1; i >= 0; i--) {
			if (_pending[i].ContainerId != container.Id) continue;

			entry.Host.Add(_pending[i]);
			_pending.RemoveAt(i);
		}

		Changed?.Invoke();
		Refresh();
	}

	public void RegisterView(ViewDescriptor view) => _post(() => AddView(view));

	public void AddView(ViewDescriptor view) {
		if (!_containers.TryGetValue(view.ContainerId, out Entry? entry)) {
			_pending.Add(view);
			return;
		}

		entry.Host.Add(view);
		Changed?.Invoke();
		Refresh();
	}

	private void Place(Entry entry) {
		Part(entry.Location).Attach(entry.Host);

		if (entry.Location == ViewLocation.Sidebar) _workbench.ActivityBar.AddContainer(entry.Container);
	}

	public void Move(string containerId, ViewLocation location) {
		if (!_containers.TryGetValue(containerId, out Entry? entry) || entry.Location == location || !entry.Container.Movable) return;

		bool wasShown = IsContainerVisible(containerId);
		Part(entry.Location).Detach(entry.Host);

		if (entry.Location == ViewLocation.Sidebar) {
			_workbench.ActivityBar.RemoveContainer(containerId);

			if (_workbench.ActivityBar.ActiveId == containerId) ShowFirstSidebarContainer();
		}

		entry.Location = location;
		Place(entry);

		if (location == entry.Container.Location) SavedLocations.Remove(containerId);
		else SavedLocations[containerId] = location.ToString();

		ShowContainer(containerId);

		if (wasShown && location != ViewLocation.Sidebar && _workbench.Sidebar.ActiveContainer is null) _workbench.ShowSidebar = false;

		Changed?.Invoke();
		Refresh();
	}

	private void ShowFirstSidebarContainer() {
		Entry? first = null;

		foreach (Entry entry in _containers.Values) {
			if (entry.Location == ViewLocation.Sidebar && (first is null || entry.Container.Order < first.Container.Order)) first = entry;
		}

		_workbench.ActivityBar.ActiveId = first?.Container.Id;
		_workbench.Sidebar.ShowContainer(first?.Container.Id);
	}

	public void ShowContainer(string containerId) {
		if (!_containers.TryGetValue(containerId, out Entry? entry)) {
			_post(() => {
				if (_containers.ContainsKey(containerId)) ShowContainer(containerId);
			});
			return;
		}

		switch (entry.Location) {
			case ViewLocation.Sidebar:
				_workbench.ActivityBar.ActiveId = containerId;
				_workbench.Sidebar.ShowContainer(containerId);
				_workbench.ShowSidebar = true;
				break;

			case ViewLocation.Auxiliary:
				_workbench.Auxiliary.Select(containerId);
				_workbench.Auxiliary.IsOpen = true;
				break;

			default:
				_workbench.Panel.ShowContainer(containerId);
				break;
		}

		ContainerShown?.Invoke(containerId);
		Refresh();
	}

	public void ToggleContainer(string containerId) {
		if (IsContainerVisible(containerId) && _containers[containerId].Location == ViewLocation.Sidebar) {
			_workbench.ShowSidebar = false;
			Refresh();
			return;
		}

		ShowContainer(containerId);
	}

	public bool IsContainerVisible(string containerId) {
		if (!_containers.TryGetValue(containerId, out Entry? entry)) return false;

		return entry.Location switch {
			ViewLocation.Sidebar => _workbench.ShowSidebar && _workbench.Sidebar.ActiveContainer == containerId,
			ViewLocation.Auxiliary => _workbench.Auxiliary.IsOpen && _workbench.Auxiliary.ActiveContainer == containerId,
			_ => _workbench.Panel.IsOpen && _workbench.Panel.ActiveContainer == containerId,
		};
	}

	public ViewLocation? LocationOf(string containerId) => _containers.TryGetValue(containerId, out Entry? entry) ? entry.Location : null;

	public void SetBadge(string containerId, int count) {
		_workbench.ActivityBar.SetBadge(containerId, count);
		_workbench.Auxiliary.Strip.SetBadge(containerId, count);
	}

	#region Drag

	public void DragStarted(string containerId, float x, float y) {
		_dragging = containerId;

		Panel? parent = OverlayParent;

		if (parent is not null && !ReferenceEquals(_overlay.Parent, parent)) parent.AddChild(_overlay);

		_overlay.Visible = true;
		_overlay.SetBounds(0f, 0f, parent?.Wide ?? 0f, parent?.Tall ?? 0f);
		DragMoved(x, y);
	}

	public void DragMoved(float x, float y) {
		(_, float height) = WindowSize();

		AuxiliaryStrip strip = _workbench.Auxiliary.Strip;
		bool container = _dragging is { } dragged && _containers.ContainsKey(dragged);

		if (container && Contains(_workbench.ActivityBarRect(height), x, y)) {
			_workbench.ActivityBar.DropIndex = _workbench.ActivityBar.DropIndexAt(y);
			strip.DropIndex = null;
			_overlay.Highlight = null;
		}
		else if (strip.ContainsPoint(x, y) && (container || strip.Contains(_dragging!))) {
			strip.DropIndex = strip.DropIndexAt(y);
			_workbench.ActivityBar.DropIndex = null;
			_overlay.Highlight = null;
		}
		else {
			_workbench.ActivityBar.DropIndex = null;
			strip.DropIndex = null;
			_overlay.Highlight = container ? TargetAt(x, y)?.Item2 : null;
		}

		_overlay.Repaint();
	}

	public void DragEnded(float x, float y, bool cancelled) {
		string? id = _dragging;
		int? dropIndex = cancelled ? null : _workbench.ActivityBar.DropIndex;
		int? stripIndex = cancelled ? null : _workbench.Auxiliary.Strip.DropIndex;
		ViewLocation? target = cancelled || dropIndex is not null || stripIndex is not null ? null : TargetAt(x, y)?.Item1;

		_workbench.Auxiliary.Strip.DropIndex = null;

		if (id is not null && stripIndex is { } auxiliaryIndex) {
			if (!_workbench.Auxiliary.Strip.Contains(id)) Move(id, ViewLocation.Auxiliary);

			_workbench.Auxiliary.Strip.MoveEntry(id, auxiliaryIndex);
		}

		_dragging = null;
		_overlay.Highlight = null;
		_overlay.Visible = false;
		_workbench.ActivityBar.DropIndex = null;

		if (stripIndex is not null) {
		}
		else if (id is not null && dropIndex is { } index) {
			if (!_workbench.ActivityBar.Contains(id)) Move(id, ViewLocation.Sidebar);

			_workbench.ActivityBar.MoveEntry(id, index);
		}
		else if (id is not null && target is { } location) {
			Move(id, location);
		}

		Refresh();
	}

	private (ViewLocation, PixelRect)? TargetAt(float x, float y) {
		(float width, float height) = WindowSize();
		float scale = _workbench.Scale;
		float top = _workbench.ShellTop;
		float bottom = _workbench.StatusBarTop(height);

		PixelRect sidebar = _workbench.ShowSidebar ? Union(_workbench.ActivityBarRect(height), _workbench.SidebarRect(height)) : PixelRect.Around(0f, top, _workbench.ActivityBarWidth + (48f * scale), bottom - top);
		PixelRect auxiliary = _workbench.AuxiliaryWidth > 0f ? PixelRect.Around(_workbench.AuxiliaryLeft(width), top, width - _workbench.AuxiliaryLeft(width), bottom - top) : PixelRect.Around(width - MathF.Max(_workbench.AuxiliaryStripWidth, 48f * scale), top, MathF.Max(_workbench.AuxiliaryStripWidth, 48f * scale), bottom - top);
		PixelRect panel = _workbench.Panel.IsOpen ? _workbench.PanelRect(width, height) : PixelRect.Around(_workbench.EditorLeft, bottom - (120f * scale), _workbench.EditorWidth(width), 120f * scale);

		if (Contains(sidebar, x, y)) return (ViewLocation.Sidebar, sidebar);

		if (Contains(auxiliary, x, y)) return (ViewLocation.Auxiliary, auxiliary);

		if (Contains(panel, x, y)) return (ViewLocation.Panel, panel);

		return null;
	}

	private static bool Contains(PixelRect rect, float x, float y) => x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;

	private static PixelRect Union(PixelRect left, PixelRect right) => new(Math.Min(left.Left, right.Left), Math.Min(left.Top, right.Top), Math.Max(left.Right, right.Right), Math.Max(left.Bottom, right.Bottom));

	private sealed class DropOverlay : Panel
	{
		public DropOverlay() {
			Name = "DropOverlay";
			MouseInputEnabled = false;
			Visible = false;
		}

		public PixelRect? Highlight {
			get;
			set {
				if (Nullable.Equals(field, value)) return;

				if (field is { } previous) Ui?.Repaint(previous);

				field = value;

				if (value is { } next) Ui?.Repaint(next);
			}
		}

		public override Panel? IsWithinTraverse(float screenX, float screenY) => null;

		public override void Paint(IPainter painter) {
			if (Highlight is not { } rect) return;

			float scale = Scale;
			painter.FillRoundedRectangle(rect.Left, rect.Top, rect.Width, rect.Height, Metrics.RadiusLarge * scale, Theme.Accent.WithAlpha(0.14f));
			painter.StrokeRoundedRectangle(rect.Left + scale, rect.Top + scale, rect.Width - (2f * scale), rect.Height - (2f * scale), Metrics.RadiusLarge * scale, 2f * scale, Theme.Accent.WithAlpha(0.8f));
		}
	}

	#endregion

	#region Editors

	public T OpenEditor<T>(string id, Func<T> create, bool preserveFocus = false) where T : EditorPane {
		T pane;

		lock (_gate) {
			if (_editors.TryGetValue(id, out EditorPane? existing) && existing is T typed) pane = typed;
			else {
				pane = create();
				pane.Id = id;
				_editors[id] = pane;
			}
		}

		_post(() => {
			_workbench.OpenPane(pane);

			if (!preserveFocus) pane.RequestFocus();

			Refresh();
		});

		return pane;
	}

	public T? FindEditor<T>(string id) where T : EditorPane {
		lock (_gate) return _editors.TryGetValue(id, out EditorPane? pane) ? pane as T : null;
	}

	public void Forget(EditorPane pane) {
		lock (_gate) {
			if (_editors.TryGetValue(pane.Id, out EditorPane? existing) && ReferenceEquals(existing, pane)) _editors.Remove(pane.Id);
		}
	}

	public void Post(Action action) => _post(action);

	public void CloseEditor(string id) => _post(() => {
		_workbench.ClosePane(id);
		Refresh();
	});

	#endregion
}
