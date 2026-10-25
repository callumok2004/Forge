using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Common.Workspace;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class Sidebar : Panel
{
	private const string RunTitle = "RUN AND DEBUG";
	private const string SourceControlTitle = "SOURCE CONTROL";
	private const float HeaderActionSize = 22f;
	private const float HeaderPaddingRight = 8f;

	private readonly Panel _header;
	private readonly Label _title;

	public Sidebar() {
		Name = "Sidebar";
		MouseInputEnabled = false;

		_header = new Panel { Name = "SidebarHeader", Dock = DockType.Top, MouseInputEnabled = false };
		_title = new Label { Name = "SidebarTitle", Dock = DockType.Fill, Font = FontRole.UiStrong, Foreground = Theme.SidebarTitleForeground, Padding = new Thickness(Metrics.Space8 + Metrics.Space4, 0f, 0f, 0f) };
		_header.AddChild(_title);

		foreach (ActionButton action in SourceControl.HeaderActions) {
			action.Dock = DockType.Right;
			_header.AddChild(action);
		}

		AddChild(_header);
		AddChild(Explorer);
		AddChild(SourceControl);
		AddChild(Debug);
		SyncView();
	}

	public ExplorerView Explorer { get; } = new();

	public SourceControlView SourceControl { get; } = new();

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

	public string Title {
		get;
		set {
			field = value;
			SyncView();
		}
	} = "EXPLORER";

	public IGitRepository? Repository {
		get;
		set {
			field = value;
			Explorer.Repository = value;
			SourceControl.Repository = value;
			Repaint();
		}
	}

	public ActivityView View {
		get;
		set {
			if (field == value) return;

			field = value;
			SyncView();
		}
	} = ActivityView.Explorer;

	public void SetTree(IFileTree tree) => Explorer.SetTree(tree);

	public void Resize(float width) => Width = Math.Clamp(width, Metrics.SidebarMinWidth, Metrics.SidebarMaxWidth);

	private void SyncView() {
		bool scm = View == ActivityView.SourceControl;

		_title.SetText(View switch {
			ActivityView.Run => RunTitle,
			ActivityView.SourceControl => SourceControlTitle,
			_ => Title,
		});

		foreach (ActionButton action in SourceControl.HeaderActions) action.Visible = scm;

		Explorer.Visible = View is not (ActivityView.SourceControl or ActivityView.Run);
		SourceControl.Visible = scm;
		Debug.Visible = View == ActivityView.Run;
		Repaint();
	}

	public override void PerformLayout() {
		float scale = Scale;
		float height = Metrics.SidebarHeaderHeight * scale;
		float action = HeaderActionSize * scale;

		_header.SetSize(Wide, height);
		_header.DockPadding = new Thickness(0f, 0f, HeaderPaddingRight * scale, 0f);

		foreach (ActionButton button in SourceControl.HeaderActions) {
			button.SetSize(action, action);
			button.DockMargin = new Thickness(0f, (height - action) / 2f, 0f, (height - action) / 2f);
		}

		base.PerformLayout();
	}

	public override void PaintBackground(IPainter painter) => painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Theme.ChromeBackground);

	public override void PaintBorder(IPainter painter) => painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Metrics.StrokeThickness * Scale, Theme.PanelBorder);
}
