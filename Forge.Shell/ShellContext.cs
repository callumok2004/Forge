using Forge.Common.Render;
using Forge.Core;
using Forge.Debug;
using Forge.Editor;
using Forge.Render;
using Forge.UI;
using Forge.UI.Controls;

namespace Forge.Shell;

public sealed class ShellContext(Workbench workbench, AppWindow window, RenderDevice device, IPainter painter, Settings settings, DebugService debugger, string rootPath)
{
	public Workbench Workbench { get; } = workbench;

	public AppWindow Window { get; } = window;

	public RenderDevice Device { get; } = device;

	public IPainter Painter { get; } = painter;

	public Settings Settings { get; } = settings;

	public DebugService Debugger { get; } = debugger;

	public string RootPath { get; } = rootPath;

	public required Action Refresh { get; init; }

	public required Action RefreshEditor { get; init; }

	public required Action ApplyZoom { get; init; }

	public required Action<PixelRect> Damage { get; init; }

	public required Func<float, bool> SetCodeZoom { get; init; }

	private const int MinEditorZoom = -5;
	private const int MaxEditorZoom = 20;
	private const float EditorZoomStep = 0.1f;

	private int _editorZoom;

	public void ZoomEditor(int step) {
		_editorZoom = step == 0 ? 0 : Math.Clamp(_editorZoom + step, MinEditorZoom, MaxEditorZoom);

		if (!SetCodeZoom(1f + (_editorZoom * EditorZoomStep))) return;

		RevealCaret();
		Refresh();
	}

	public CommandHost Host => field ??= new CommandHost(Workbench, Refresh);

	public static bool IsFocused(string contextKey) => Common.UI.Panel.Ui?.HasContext(contextKey) ?? false;

	public static bool EditorFocused => IsFocused(Common.UI.ContextKeys.EditorTextFocus);

	public void Run(string commandId) {
		Workbench.RunCommand(commandId, Host);
		Refresh();
	}

	public void RevealCaret() {
		if (Workbench.ActiveView is not { } view) return;

		view.EnsureCaretVisible(Painter.GetCode(), Workbench.TextWidth(Device.Width), Workbench.EditorHeight(Device.Height));
		RefreshEditor();
	}

	public void ScrollToMatch(bool found) {
		if (found) RevealCaret();
	}

	private Menu? _applicationMenu;
	private Menu? _openMenu;

	public void OpenMenu(MenuEntry[] entries, float left) {
		bool application = entries == ApplicationMenu.CodeMenu;
		OpenMenu(application ? _applicationMenu ??= ApplicationMenu.Build(entries) : ApplicationMenu.Build(entries), left, Workbench.ShellTop, application);
	}

	public void OpenMenu(Menu menu, float left, float top, bool application = false) {
		CloseMenu();

		_openMenu = menu;
		Workbench.TitleBar.MenuOpen = application;
		menu.Closed = () => {
			Workbench.TitleBar.MenuOpen = false;
			Damage(Workbench.TitleBarRect(Device.Width));
		};

		menu.Open(left, top);
		Damage(Workbench.TitleBarRect(Device.Width));
	}

	public void CloseMenu() => _openMenu?.CloseAll();

	public TextView? ActiveView => Workbench.ActiveView;
}
