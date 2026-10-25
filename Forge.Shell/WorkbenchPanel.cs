using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Shell;

public sealed class WorkbenchPanel : Panel
{
	private readonly Workbench _workbench;
	private readonly EditorPart _editor;

	public WorkbenchPanel(Workbench workbench) {
		_workbench = workbench;
		Name = "Workbench";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		AddChild(workbench.TitleBar);
		AddChild(workbench.StatusBar);
		AddChild(workbench.ActivityBar);
		AddChild(workbench.Sidebar);
		AddChild(workbench.Auxiliary);
		AddChild(workbench.Auxiliary.Strip);
		AddChild(_editor = new EditorPart(workbench));
		AddChild(workbench.Panel);
		AddChild(_sidebarSash = new Splitter(vertical: true) { Name = "SidebarSash", Dragged = (x, _) => Resize(() => workbench.ResizeSidebar(x)) });
		AddChild(_auxiliarySash = new Splitter(vertical: true) { Name = "AuxiliarySash", Dragged = (x, _) => Resize(() => workbench.ResizeAuxiliary(x, Wide)) });
		AddChild(_panelSash = new Splitter(vertical: false) { Name = "PanelSash", Dragged = (_, y) => Resize(() => workbench.ResizePanel(y, Tall)) });
		AddChild(workbench.FrameStatsOverlay);
		AddChild(workbench.Notifications);
	}

	private readonly Splitter _sidebarSash;
	private readonly Splitter _auxiliarySash;
	private readonly Splitter _panelSash;

	public Action? Resized { get; set; }

	public EditorContent Editor => _editor.Content;

	private void Resize(Action resize) {
		resize();
		Resized?.Invoke();
	}

	public void SyncLegacyLayout(float windowWidth, float windowHeight) {
		float scale = Scale;
		float outer = Metrics.CardMargin * scale;
		float inner = Metrics.CardInnerMargin * scale;
		float top = _workbench.ShellTop;
		float bottom = _workbench.StatusBarTop(windowHeight);

		Sidebar sidebar = _workbench.Sidebar;
		sidebar.Visible = _workbench.ShowSidebar;
		sidebar.SetBounds(_workbench.ActivityBarWidth + outer, top, _workbench.SidebarWidth - outer - inner, bottom - top - inner);

		AuxiliaryBar auxiliary = _workbench.Auxiliary;
		auxiliary.Visible = _workbench.AuxiliaryWidth > 0f;
		auxiliary.SetBounds(_workbench.AuxiliaryLeft(windowWidth), top, _workbench.AuxiliaryWidth - inner, bottom - top - inner);
		auxiliary.Strip.Visible = _workbench.AuxiliaryStripWidth > 0f;
		auxiliary.Strip.SetBounds(_workbench.AuxiliaryStripLeft(windowWidth), top, _workbench.AuxiliaryStripWidth, bottom - top);

		PanelPart panel = _workbench.Panel;
		panel.Visible = panel.IsOpen;
		panel.SetBounds(_workbench.EditorLeft, _workbench.PanelTop(windowHeight), _workbench.EditorWidth(windowWidth), panel.Height * scale);

		_editor.Sync(windowWidth, windowHeight);

		float hit = Metrics.SplitterHitWidth * scale;
		float sidebarEdge = _workbench.ActivityBarWidth + _workbench.SidebarWidth;
		float panelTop = _workbench.PanelTop(windowHeight);

		_sidebarSash.Visible = _workbench.ShowSidebar;
		_sidebarSash.SetBounds(sidebarEdge - hit, top, hit * 2f, bottom - top);
		_auxiliarySash.Visible = auxiliary.Visible;
		_auxiliarySash.SetBounds(_workbench.AuxiliaryLeft(windowWidth) - hit, top, hit * 2f, bottom - top);
		_panelSash.Visible = panel.IsOpen;
		_panelSash.SetBounds(_workbench.EditorLeft, panelTop - hit, _workbench.EditorWidth(windowWidth), hit * 2f);

		float statsWidth = FrameStatsOverlay.Width * scale;
		float statsHeight = FrameStatsOverlay.BadgeHeight * scale;
		_workbench.FrameStatsOverlay.SetBounds(windowWidth - _workbench.AuxiliaryWidth - (Metrics.SpaceLg * scale) - statsWidth, bottom - statsHeight - (Metrics.SpaceSm * scale), statsWidth, statsHeight);

		NotificationToasts notifications = _workbench.Notifications;
		float toastWidth = MathF.Min(NotificationToast.Width * scale, windowWidth - (Metrics.SpaceLg * 2f * scale));
		float toastHeight = notifications.ContentHeight;
		notifications.Visible = toastHeight > 0f;
		notifications.SetBounds(windowWidth - toastWidth - (Metrics.SpaceLg * scale), bottom - toastHeight - (Metrics.SpaceSm * scale), toastWidth, toastHeight);
	}

	public override void PerformLayout() {
		_workbench.TitleBar.SetSize(Wide, TitleBar.Height(Scale));
		_workbench.StatusBar.SetSize(Wide, StatusBar.Height(Scale));
		base.PerformLayout();
	}

	public override void PaintBackground(IPainter painter) {
		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.EditorBackground);
		_workbench.PaintBackdrop?.Invoke();
	}
}
