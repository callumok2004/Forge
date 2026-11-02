using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Common.Webview;
using Forge.Core;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.VSCode;

namespace Forge.Workbench;

public sealed class AuxiliaryBar : Panel, IViewPart
{
	public const float MinWidth = 240f;
	public const float MaxWidth = 900f;
	public const string Location = "secondarySidebar";

	private const float CloseSize = 20f;

	private readonly Dictionary<string, string> _html = new(StringComparer.Ordinal);
	private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
	private readonly Panel _header;
	private readonly Label _title;
	private readonly Label _status;
	private readonly ActionButton _close;
	private readonly WebviewSurface _surface;

	private IReadOnlyList<WebviewViewEntry> _views = [];
	private readonly ViewPartHost _host;

	public AuxiliaryBar() {
		Name = "AuxiliaryBar";
		MouseInputEnabled = false;

		_header = new Panel { Name = "AuxiliaryHeader", Dock = DockType.Top, MouseInputEnabled = false };
		_title = new Label { Name = "AuxiliaryTitle", Dock = DockType.Fill, Font = FontRole.UiStrong, Foreground = Theme.SidebarTitleForeground, Padding = new Thickness(Metrics.Space12, 0f, 0f, 0f) };
		_close = new ActionButton { Name = "AuxiliaryClose", Icon = Codicon.Close, Dock = DockType.Right, Foreground = Theme.SectionHeaderForeground, HoverForeground = Theme.TabActiveForeground, HoverBackground = Theme.ListHoverBackground, Command = "workbench.action.toggleAuxiliaryBar" };
		_status = new Label { Name = "AuxiliaryStatus", Dock = DockType.Top, Foreground = Theme.SectionHeaderForeground, Padding = new Thickness(Metrics.Space12, 0f, 0f, 0f), Visible = false };

		_header.AddChild(_close);
		_header.AddChild(_title);
		AddChild(_header);
		AddChild(_status);
		AddChild(_surface = new WebviewSurface(this));
		Strip = new AuxiliaryStrip(this);
		_host = new ViewPartHost(this, _header, SyncTitle);
	}

	public string? ActiveContainer => _host.Active?.Container.Id;

	public IReadOnlyList<ViewPaneContainer> Containers => _host.Containers;

	public void Attach(ViewPaneContainer container) {
		_host.Attach(container);
		Strip.Rebuild();
	}

	public void Detach(ViewPaneContainer container) {
		bool wasActive = ReferenceEquals(_host.Active, container);
		_host.Detach(container);

		if (wasActive) {
			ActiveViewId = _host.Containers.Count > 0 ? _host.Containers[0].Container.Id : _views.Count > 0 ? _views[0].ViewId : null;

			if (ActiveViewId is { } next) ShowView(next);
			else _surface.Visible = true;
		}

		Strip.Rebuild();
		SyncTitle();
	}

	public void ShowContainer(string? id) {
		if (id is null) return;

		ShowView(id);
	}

	private void ShowView(string id) {
		bool container = _host.Containers.Any(candidate => candidate.Container.Id == id);
		ActiveViewId = id;
		_host.Show(container ? id : null);
		_surface.Visible = !container;
		Strip.Sync();
		SyncTitle();
		InvalidateLayout();
	}

	public AuxiliaryStrip Strip { get; }

	public bool IsOpen {
		get;
		set {
			if (field == value) return;

			field = value;
			Strip.Sync();
		}
	}

	public float Width { get; set; } = 380f;

	public string? ActiveViewId { get; private set; }

	public string? Status {
		get;
		set {
			field = value;
			_status.Visible = value is { Length: > 0 };
			_status.SetText(value);
		}
	}

	public PixelRect ContentBounds { get; private set; }

	public IReadOnlyList<WebviewViewEntry> Views => _views;

	public bool HasViews => _views.Count > 0 || _host.Containers.Count > 0;

	public WebviewViewEntry? Active => ActiveViewId is { } id ? Find(id) : null;

	public string? ShownViewId {
		get;
		set {
			if (field == value) return;

			field = value;
			_surface.Repaint();
		}
	}

	public IWebviewHost Webviews { get; set; } = NullWebviewHost.Instance;

	public Func<string, IVectorIcon?>? IconLoader {
		get => Strip.IconLoader;
		set => Strip.IconLoader = value;
	}

	public void SetViews(IReadOnlyList<WebviewViewEntry> views) {
		List<WebviewViewEntry> mine = [];

		foreach (WebviewViewEntry view in views)
			if (string.Equals(view.Location, Location, StringComparison.Ordinal)) mine.Add(view);

		_views = mine;

		if (ActiveViewId is not { } active || (Find(active) is null && !_host.Containers.Any(container => container.Container.Id == active))) ActiveViewId = _host.Containers.Count > 0 ? _host.Containers[0].Container.Id : mine.Count > 0 ? mine[0].ViewId : null;

		Strip.Rebuild();
		SyncTitle();
	}

	public string? HtmlFor(string viewId) => _html.TryGetValue(viewId, out string? html) ? html : null;

	public void SetHtml(string viewId, string html) => _html[viewId] = html;

	public bool ClaimResolve(string viewId) => _requested.Add(viewId);

	public WebviewViewEntry? Find(string viewId) {
		foreach (WebviewViewEntry view in _views)
			if (string.Equals(view.ViewId, viewId, StringComparison.Ordinal)) return view;

		return null;
	}

	public bool Select(string viewId) {
		if ((Find(viewId) is null && !_host.Containers.Any(container => container.Container.Id == viewId)) || string.Equals(ActiveViewId, viewId, StringComparison.Ordinal)) return false;

		ShowView(viewId);
		return true;
	}

	public void RestoreSelection(string viewId) {
		ActiveViewId = viewId;

		if (_host.Containers.Any(container => container.Container.Id == viewId)) ShowView(viewId);
		else SyncTitle();
	}

	public void Resize(float width) => Width = Math.Clamp(width, MinWidth, MaxWidth);

	public bool Toggle() {
		if (!HasViews) return false;

		IsOpen = !IsOpen;
		return true;
	}

	public void Activate(string viewId) {
		if (IsOpen && string.Equals(ActiveViewId, viewId, StringComparison.Ordinal)) IsOpen = false;
		else {
			Select(viewId);
			IsOpen = true;
		}

		Activated?.Invoke();
	}

	public Action? Activated { get; set; }

	private void SyncTitle() => _title.SetText(_host.Active?.Container.DisplayTitle ?? Active?.Title);

	public override void PerformLayout() {
		float scale = Scale;
		float header = Metrics.SidebarHeaderHeight * scale;
		float close = CloseSize * scale;
		float stroke = Metrics.StrokeThickness * scale;

		_header.SetSize(Wide, header);
		_header.DockPadding = new Thickness(0f, 0f, (Metrics.Space12 - ((CloseSize - Metrics.IconSize) / 2f)) * scale, 0f);
		_close.SetSize(close, close);
		_close.DockMargin = new Thickness(0f, (header - close) / 2f, 0f, (header - close) / 2f);
		_status.SetSize(Wide, header);
		_host.LayoutActions(close, header);

		_surface.DockMargin = new Thickness(stroke, 0f, stroke, stroke);

		base.PerformLayout();

		(float x, float y) = _surface.GetAbsPos();
		ContentBounds = new PixelRect((int)MathF.Ceiling(x), (int)MathF.Ceiling(y), (int)MathF.Floor(x + _surface.Wide), (int)MathF.Floor(y + _surface.Tall));
	}

	public override void PaintBackground(IPainter painter) {
		float radius = Metrics.RadiusLarge * Scale;
		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.SidebarBackground);
		painter.FillRectangle(ScreenX, ScreenY + Tall - radius, Wide, radius, Theme.SidebarBackground);
	}

	public override void PaintBorder(IPainter painter) => painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Metrics.StrokeThickness * Scale, Theme.PanelBorder);
}
