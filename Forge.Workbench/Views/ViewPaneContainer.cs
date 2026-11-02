using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ViewPaneContainer : Panel
{
	public const float HeaderHeight = 22f;
	public const float ActionSize = 22f;

	private readonly List<Pane> _panes = [];

	public ViewPaneContainer(ViewContainer container) {
		Container = container;
		Name = container.Id;
		Dock = DockType.Fill;
		MouseInputEnabled = false;
	}

	public ViewContainer Container { get; }

	public IReadOnlyList<ViewDescriptor> Views => [.. _panes.Select(static pane => pane.Descriptor)];

	public bool IsEmpty => _panes.Count == 0;

	public bool ShowHeaders => _panes.Count > 1;

	public event Action? TitleActionsChanged;

	public IReadOnlyList<Panel> TitleActions => _panes.Count == 1 ? _panes[0].Controls : [];

	public void Add(ViewDescriptor descriptor) {
		if (_panes.Exists(pane => pane.Descriptor.Id == descriptor.Id)) return;

		Pane pane = new(this, descriptor);
		int index = _panes.FindIndex(existing => existing.Descriptor.Order > descriptor.Order);

		if (index < 0) _panes.Add(pane);
		else _panes.Insert(index, pane);

		AddChild(pane.Header);
		AddChild(pane.Body);
		Changed();
	}

	public bool Remove(string viewId) {
		int index = _panes.FindIndex(pane => pane.Descriptor.Id == viewId);

		if (index < 0) return false;

		Pane pane = _panes[index];
		_panes.RemoveAt(index);
		RemoveChild(pane.Header);
		RemoveChild(pane.Body);
		Changed();
		return true;
	}

	private void Changed() {
		foreach (Pane pane in _panes) pane.Header.ReclaimControls();

		InvalidateLayout();
		Repaint();
		TitleActionsChanged?.Invoke();
	}

	public void Toggle(string viewId) {
		if (_panes.Find(pane => pane.Descriptor.Id == viewId) is not { } pane) return;

		pane.Collapsed = !pane.Collapsed;
		InvalidateLayout();
		Repaint();
	}

	public void SyncActions() {
		foreach (Pane pane in _panes) pane.SyncVisibility();
	}

	public override void PerformLayout() {
		float scale = Scale;
		float header = ShowHeaders ? HeaderHeight * scale : 0f;
		int expanded = 0;

		foreach (Pane pane in _panes) {
			if (!pane.Collapsed || !ShowHeaders) expanded++;
		}

		float bodyHeight = expanded == 0 ? 0f : MathF.Max(0f, (Tall - (header * _panes.Count)) / expanded);
		float y = 0f;

		foreach (Pane pane in _panes) {
			pane.Header.Visible = ShowHeaders;

			if (ShowHeaders) {
				pane.Header.SetBounds(0f, y, Wide, header);
				pane.Header.LayoutControls(scale);
				y += header;
			}

			bool open = !ShowHeaders || !pane.Collapsed;
			pane.Body.Visible = open;

			if (!open) continue;

			pane.Body.SetBounds(0f, y, Wide, bodyHeight);
			y += bodyHeight;
		}
	}

	private sealed class Pane
	{
		public Pane(ViewPaneContainer owner, ViewDescriptor descriptor) {
			Descriptor = descriptor;
			Body = descriptor.Create();
			Body.Dock = DockType.None;

			List<Panel> controls = [];

			foreach (ViewAction action in descriptor.Actions) {
				controls.Add(new ActionButton { Name = action.Title, Icon = action.Icon, Command = action.Command, Foreground = Theme.ChromeForeground, HoverBackground = Theme.ListHoverBackground, Radius = Metrics.RadiusSmall });
				Visibility.Add(action.Visible);
			}

			if (descriptor.TitleControls?.Invoke() is { } extra) {
				foreach (Panel control in extra) {
					controls.Add(control);
					Visibility.Add(null);
				}
			}

			Controls = controls;
			Header = new PaneHeader(owner, this);
		}

		public ViewDescriptor Descriptor { get; }

		public Panel Body { get; }

		public PaneHeader Header { get; }

		public IReadOnlyList<Panel> Controls { get; }

		public List<Func<bool>?> Visibility { get; } = [];

		public bool Collapsed { get; set; }

		public void SyncVisibility() {
			for (int i = 0; i < Controls.Count; i++) {
				if (Visibility[i] is { } visible) Controls[i].Visible = visible();
			}
		}
	}

	private sealed class PaneHeader : Panel
	{
		private readonly ViewPaneContainer _owner;
		private readonly Pane _pane;
		private readonly string _title;

		public PaneHeader(ViewPaneContainer owner, Pane pane) {
			_owner = owner;
			_pane = pane;
			_title = pane.Descriptor.ContainerId == ViewContainer.ExplorerId ? pane.Descriptor.Name : TextCase.Capitalize(pane.Descriptor.Name);
			Name = pane.Descriptor.Id + ".header";
		}

		public void ReclaimControls() {
			if (!_owner.ShowHeaders) return;

			foreach (Panel control in _pane.Controls) {
				if (!ReferenceEquals(control.Parent, this)) AddChild(control);
			}
		}

		public void LayoutControls(float scale) {
			_pane.SyncVisibility();

			float size = ActionSize * scale;
			float x = Wide - (Metrics.Space8 * scale);

			for (int i = _pane.Controls.Count - 1; i >= 0; i--) {
				Panel control = _pane.Controls[i];

				if (!control.Visible || !ReferenceEquals(control.Parent, this)) continue;

				x -= size;
				control.SetBounds(x, (Tall - size) / 2f, size, size);
			}
		}

		public override void OnCursorEntered() => Repaint();

		public override void OnCursorExited() => Repaint();

		public override bool OnMousePressed(MouseButton button, float x, float y) {
			if (button != MouseButton.Left) return false;

			_owner.Toggle(_pane.Descriptor.Id);
			return true;
		}

		public override void Paint(IPainter painter) {
			float scale = Scale;
			ITextFont font = painter.GetUi(11f, strong: true);
			float centreY = ScreenY + (Tall / 2f);
			float left = ScreenX + (4f * scale);

			if (IsCursorOver) painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.ListHoverBackground);

			painter.DrawHorizontalLine(ScreenY, ScreenX, ScreenX + Wide, Theme.ChromeBorder);
			painter.DrawChevron(left + (8f * scale), centreY, 8f * scale, !_pane.Collapsed, Theme.SidebarTitleForeground);
			painter.DrawText(font, _title, left + (20f * scale), ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline(), Theme.SidebarTitleForeground);
		}
	}
}
