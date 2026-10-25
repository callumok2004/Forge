using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Shell;

public sealed class EditorPart : Panel
{
	private readonly Workbench _workbench;

	public EditorPart(Workbench workbench) {
		_workbench = workbench;
		Name = "EditorPart";
		MouseInputEnabled = false;

		AddChild(workbench.Tabs);
		AddChild(workbench.Breadcrumbs);
		AddChild(Content = new EditorContent(workbench) { ContextKey = ContextKeys.EditorTextFocus });
		AddChild(workbench.Find);
	}

	public EditorContent Content { get; }

	public void Sync(float windowWidth, float windowHeight) {
		float top = _workbench.ShellTop;

		SetBounds(_workbench.EditorLeft, top, _workbench.EditorWidth(windowWidth), _workbench.EditorBottom(windowHeight) - top);

		_workbench.Tabs.Sync(_workbench.Documents, _workbench.ActiveIndex);
		_workbench.Breadcrumbs.Visible = _workbench.ActiveView is not null;

		if (_workbench.IntelliSense is not { } intelliSense) return;

		if (!_widgetsAttached) {
			_widgetsAttached = true;

			foreach (Panel widget in intelliSense.Widgets) AddChild(widget);
		}

		float left = _workbench.EditorLeft;

		if (_workbench.DebugStops is { } stops && _workbench.ActiveView is { } view && Ui is { } ui) {
			if (stops.Widget.Parent is null) AddChild(stops.Widget);

			float gutter = view.GutterWidth(ui.GetFonts().GetCode());
			stops.Place(left + gutter, _workbench.EditorTop, _workbench.TextWidth(windowWidth) - gutter, ui.GetFonts().GetCode().GetLineHeight(), left, top);
		}

		if (_workbench.Peek is { } peek && Ui is { } peekUi) {
			if (peek.Widget.Parent is null) AddChild(peek.Widget);

			peek.Place(left, _workbench.EditorTop, _workbench.TextWidth(windowWidth), _workbench.EditorHeight(windowHeight), peekUi.GetFonts().GetCode(), left, top);
		}

		intelliSense.Place(left, top, left, _workbench.EditorTop, _workbench.EditorBottom(windowHeight), left + _workbench.EditorWidth(windowWidth));
	}

	private bool _widgetsAttached;

	public override void PerformLayout() {
		float scale = Scale;

		_workbench.Tabs.SetSize(Wide, TabStrip.Height(scale));
		_workbench.Breadcrumbs.SetSize(Wide, Breadcrumbs.Height(scale));

		base.PerformLayout();

		FindWidget find = _workbench.Find;
		float offset = FindWidget.Offset * scale;
		find.SetPos(Wide - (FindWidget.Width * scale) - offset, _workbench.EditorTop - _workbench.ShellTop + offset);
	}

	public override void PaintBackground(IPainter painter) {
		painter.PushRoundedClip(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale);
		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.EditorBackground);
	}

	public override void PostChildPaint(IPainter painter) => painter.PopRoundedClip();

	public override void PaintBorder(IPainter painter) => painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Metrics.StrokeThickness * Scale, Theme.PanelBorder);
}
