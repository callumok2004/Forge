using Forge.Common.Render;
using Forge.Core;
using Forge.Core;
using Forge.Debug;
using Forge.Editor;
using Forge.Render;
using Forge.UI;

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

	private readonly List<InputTarget> _targets = [];

	public CommandHost Host => field ??= new CommandHost(Workbench, Refresh);

	public void AddTarget(InputTarget target) => _targets.Add(target);

	public InputTarget? Focused {
		get {
			foreach (InputTarget target in _targets)
				if (target.IsActive()) return target;

			return null;
		}
	}

	public bool IsFocused(string name) => string.Equals(Focused?.Name, name, StringComparison.Ordinal);

	public bool EditorFocused => IsFocused(InputTargets.Editor);

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

	public void OpenMenu(MenuEntry[] entries, float left) {
		Workbench.Menu.Open(entries, left, Workbench.ShellTop);
		Workbench.TitleBar.MenuOpen = entries == MenuPopup.CodeMenu;
	}

	public void CloseMenu() {
		Workbench.Menu.Close();
		Workbench.TitleBar.MenuOpen = false;
	}

	public void Invoke(string? commandId) {
		CloseMenu();

		if (commandId is not null) Run(commandId);
		else Refresh();
	}

	public TextView? ActiveView => Workbench.ActiveView;
}
