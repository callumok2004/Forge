using Forge.Common.Render;
using Forge.Core;
using Forge.Theming;

namespace Forge.UI;

public readonly record struct MenuEntry(string? Label, string? CommandId = null, MenuEntry[]? Children = null)
{
	public bool IsSeparator => Label is null;

	public static MenuEntry Separator => new(null);

	public static MenuEntry Submenu(string label, MenuEntry[] children) => new(label, Children: children);
}

public sealed class MenuPopup
{
	private const float RowHeight = 22f;
	private const float SeparatorHeight = 7f;
	private const float PaddingY = 4f;
	private const float PaddingX = 10f;
	private const float KeybindingGap = 28f;
	private const float ChevronWidth = 16f;

	public static readonly MenuEntry[] CodeMenu =
	[
		MenuEntry.Submenu("File", [
			new("Open File…", "workbench.action.files.openFile"),
			new("Open Folder…", "workbench.action.files.openFolder"),
			new("New Window", "workbench.action.newWindow"),
			MenuEntry.Separator,
			new("Save", "workbench.action.files.save"),
			new("Save As…", "workbench.action.files.saveAs"),
			MenuEntry.Separator,
			new("Settings", "workbench.action.openSettings"),
			MenuEntry.Separator,
			new("Close Editor", "workbench.action.closeActiveEditor"),
			new("Exit", "workbench.action.quit"),
		]),
		MenuEntry.Submenu("Edit", [
			new("Undo", "undo"),
			new("Redo", "redo"),
			MenuEntry.Separator,
			new("Cut", "editor.action.clipboardCutAction"),
			new("Copy", "editor.action.clipboardCopyAction"),
			new("Paste", "editor.action.clipboardPasteAction"),
			MenuEntry.Separator,
			new("Find", "actions.find"),
		]),
		MenuEntry.Submenu("Selection", [
			new("Select All", "editor.action.selectAll"),
			new("Expand Line Selection", "expandLineSelection"),
			MenuEntry.Separator,
			new("Copy Line Up", "editor.action.copyLinesUpAction"),
			new("Copy Line Down", "editor.action.copyLinesDownAction"),
			new("Move Line Up", "editor.action.moveLinesUpAction"),
			new("Move Line Down", "editor.action.moveLinesDownAction"),
			MenuEntry.Separator,
			new("Add Next Occurrence", "editor.action.addSelectionToNextFindMatch"),
		]),
		MenuEntry.Submenu("View", [
			new("Command Palette…", "workbench.action.showCommands"),
			MenuEntry.Separator,
			new("Toggle Primary Side Bar", "workbench.action.toggleSidebarVisibility"),
			new("Toggle Secondary Side Bar", "workbench.action.toggleAuxiliaryBar"),
			new("Toggle Panel", "workbench.action.togglePanel"),
			new("Toggle Minimap", "editor.action.toggleMinimap"),
			MenuEntry.Separator,
			new("Terminal", "workbench.action.terminal.toggleTerminal"),
			new("Problems", "workbench.actions.view.problems"),
			new("Output", "workbench.action.output.toggleOutput"),
			new("Toggle Frame Statistics", "forge.toggleFrameStats"),
			MenuEntry.Separator,
			new("Zoom In", "workbench.action.zoomIn"),
			new("Zoom Out", "workbench.action.zoomOut"),
			new("Reset Zoom", "workbench.action.zoomReset"),
		]),
		MenuEntry.Submenu("Go", [
			new("Go to File…", "workbench.action.quickOpen"),
			MenuEntry.Separator,
			new("Go to Definition", "dotnet.goToDefinition"),
			new("Go to Implementation", "dotnet.goToImplementation"),
			new("Go to References", "dotnet.findAllReferences"),
			MenuEntry.Separator,
			new("Go to Bracket", "editor.action.jumpToBracket"),
		]),
		MenuEntry.Submenu("Run", [
			new("Start Debugging", "workbench.action.debug.start"),
			new("Run Without Debugging", "workbench.action.debug.run"),
			new("Stop Debugging", "workbench.action.debug.stop"),
			new("Restart Debugging", "workbench.action.debug.restart"),
			MenuEntry.Separator,
			new("Step Over", "workbench.action.debug.stepOver"),
			new("Step Into", "workbench.action.debug.stepInto"),
			new("Step Out", "workbench.action.debug.stepOut"),
			new("Continue", "workbench.action.debug.continue"),
			MenuEntry.Separator,
			new("Toggle Breakpoint", "editor.debug.action.toggleBreakpoint"),
			MenuEntry.Separator,
			new("Open Configurations", "workbench.action.debug.configure"),
		]),
		MenuEntry.Submenu("Terminal", [
			new("Run Task…", "workbench.action.tasks.runTask"),
			new("Run Build Task…", "workbench.action.tasks.build"),
			MenuEntry.Separator,
			new("Configure Tasks…", "workbench.action.tasks.configureTaskRunner"),
		]),
		MenuEntry.Submenu("Help", [
			new("Show All Commands", "workbench.action.showCommands"),
		]),
	];

	public Func<string, string?>? KeybindingFor { get; set; }

	public Func<string, bool>? IsEnabled { get; set; }

	private MenuEntry[] _entries = [];
	private float _anchorLeft;
	private float _anchorTop;

	private int _selected = -1;
	private int _child = -1;
	private int _childSelected = -1;
	private bool _inChild;

	private Panel _rootPanel;
	private Panel _childPanel;
	private float _renderScale = 1f;

	private readonly record struct Panel(float Left, float Top, float Width, float Height)
	{
		public bool Contains(float x, float y) => Width > 0f && x >= Left && x < Left + Width && y >= Top && y < Top + Height;
	}

	public bool IsOpen { get; private set; }

	public void Open(MenuEntry[] entries, float left, float top) {
		_entries = entries;
		_anchorLeft = left;
		_anchorTop = top;
		_selected = _child = _childSelected = -1;
		_inChild = false;
		_childPanel = default;
		IsOpen = true;
	}

	public void Close() {
		IsOpen = false;
		_entries = [];
		_selected = _child = _childSelected = -1;
		_inChild = false;
		_rootPanel = _childPanel = default;
	}

	public void MoveSelection(int delta) {
		if (!IsOpen) return;

		if (_inChild && ChildEntries is { } children) _childSelected = Step(children, _childSelected, delta);
		else {
			_selected = Step(_entries, _selected, delta);
			_child = -1;
		}
	}

	public void MoveHorizontal(int delta) {
		if (!IsOpen) return;

		if (delta > 0 && !_inChild && _selected >= 0 && _entries[_selected].Children is { } children) {
			_child = _selected;
			_inChild = true;
			_childSelected = Step(children, -1, 1);
		}
		else if (delta < 0 && _inChild) {
			_inChild = false;
			_childSelected = -1;
		}
	}

	public string? Accept() {
		if (!IsOpen) return null;

		if (_inChild && ChildEntries is { } children && _childSelected >= 0) return Pick(children[_childSelected]);

		if (_selected < 0) return null;

		if (_entries[_selected].Children is not null) {
			MoveHorizontal(1);
			return null;
		}

		return Pick(_entries[_selected]);
	}

	public bool Hover(float x, float y) {
		if (_childPanel.Contains(x, y) && ChildEntries is { } children) {
			_inChild = true;
			_childSelected = RowAt(children, _childPanel, y);
			return true;
		}

		if (_rootPanel.Contains(x, y)) {
			int row = RowAt(_entries, _rootPanel, y);

			if (row >= 0) {
				_selected = row;
				_inChild = false;
				_childSelected = -1;
				_child = _entries[row].Children is not null ? row : -1;
			}

			return true;
		}

		return false;
	}

	public PixelRect Bounds {
		get {
			if (!IsOpen) return PixelRect.Empty;

			float right = Math.Max(_rootPanel.Left + _rootPanel.Width, _childPanel.Left + _childPanel.Width);
			float bottom = Math.Max(_rootPanel.Top + _rootPanel.Height, _childPanel.Top + _childPanel.Height);

			return PixelRect.Around(_rootPanel.Left - 2f, _rootPanel.Top - 2f, right - _rootPanel.Left + 4f, bottom - _rootPanel.Top + 4f);
		}
	}

	public bool Contains(float x, float y) => IsOpen && (_rootPanel.Contains(x, y) || _childPanel.Contains(x, y));

	public string? CommandAt(float x, float y) {
		if (_childPanel.Contains(x, y) && ChildEntries is { } children) {
			int childRow = RowAt(children, _childPanel, y);
			return childRow >= 0 ? Pick(children[childRow]) : null;
		}

		int row = _rootPanel.Contains(x, y) ? RowAt(_entries, _rootPanel, y) : -1;

		return row >= 0 && _entries[row].Children is null ? Pick(_entries[row]) : null;
	}

	private MenuEntry[]? ChildEntries => _child >= 0 && _child < _entries.Length ? _entries[_child].Children : null;

	private string? Pick(MenuEntry entry) => entry.CommandId is { } id && Enabled(entry) ? id : null;

	private bool Enabled(MenuEntry entry) => entry.Children is not null || (entry.CommandId is { } id && (IsEnabled?.Invoke(id) ?? true));

	private string? KeybindingOf(MenuEntry entry) => entry.CommandId is { } id && KeybindingFor?.Invoke(id) is { } keys ? keys.Split('|')[0] : null;

	private static int Step(MenuEntry[] entries, int index, int delta) {
		for (int step = 0; step < entries.Length; step++) {
			index = ((index + delta) % entries.Length + entries.Length) % entries.Length;

			if (!entries[index].IsSeparator) return index;
		}

		return -1;
	}

	private int RowAt(MenuEntry[] entries, Panel panel, float y) {
		float cursor = panel.Top + (PaddingY * _renderScale);

		for (int i = 0; i < entries.Length; i++) {
			float height = (entries[i].IsSeparator ? SeparatorHeight : RowHeight) * _renderScale;

			if (!entries[i].IsSeparator && y >= cursor && y < cursor + height) return i;

			cursor += height;
		}

		return -1;
	}

	private float RowTop(MenuEntry[] entries, Panel panel, int row) {
		float cursor = panel.Top + (PaddingY * _renderScale);

		for (int i = 0; i < row; i++) cursor += (entries[i].IsSeparator ? SeparatorHeight : RowHeight) * _renderScale;

		return cursor;
	}

	public void Render(IPainter painter, float windowWidth, float scale) {
		if (!IsOpen) return;

		_renderScale = scale;

		_rootPanel = DrawPanel(painter, _entries, _anchorLeft, _anchorTop, windowWidth, _selected, !_inChild, scale);

		if (ChildEntries is { } children) {
			float top = RowTop(_entries, _rootPanel, _child) - (PaddingY * scale);
			float left = _rootPanel.Left + _rootPanel.Width - (Metrics.Space2 * scale);

			_childPanel = DrawPanel(painter, children, left, top, windowWidth, _childSelected, _inChild, scale);
		}
		else _childPanel = default;
	}

	private Panel DrawPanel(IPainter painter, MenuEntry[] entries, float left, float top, float windowWidth, int selected, bool focused, float scale) {
		ITextFont font = painter.GetUi();

		float padX = PaddingX * scale;
		float padY = PaddingY * scale;

		float widest = 0f;
		float content = 0f;

		foreach (MenuEntry entry in entries) {
			content += (entry.IsSeparator ? SeparatorHeight : RowHeight) * scale;

			if (entry.IsSeparator) continue;

			float trailing = entry.Children is not null ? KeybindingGap * scale : KeybindingOf(entry) is not { } keys ? 0f : (KeybindingGap * scale) + font.Measure(keys);

			widest = Math.Max(widest, font.Measure(entry.Label!) + trailing);
		}

		float width = widest + (padX * 2f) + (ChevronWidth * scale);
		float height = content + (padY * 2f);

		left = Math.Max(0f, Math.Min(left, windowWidth - width));

		float radius = Metrics.RadiusLarge * scale;

		painter.PushRoundedClip(left, top, width, height, radius);
		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.MenuBackground);

		float cursor = top + padY;

		for (int i = 0; i < entries.Length; i++) {
			MenuEntry entry = entries[i];

			if (entry.IsSeparator) {
				float ruleY = cursor + ((SeparatorHeight * scale) / 2f);
				painter.DrawHorizontalLine(ruleY, left + padX, left + width - padX, Theme.MenuSeparatorBackground);
				cursor += SeparatorHeight * scale;
				continue;
			}

			float rowHeight = RowHeight * scale;
			bool highlighted = i == selected;

			if (highlighted) {
				float inset = Metrics.Space4 * scale;
				painter.FillRoundedRectangle(left + inset, cursor, width - (inset * 2f), rowHeight, Metrics.RadiusSmall * scale, focused ? Theme.MenuSelectionBackground : Theme.MenubarSelectionBackground);
			}

			float baseline = cursor + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

			Color color = !Enabled(entry) ? Theme.DisabledForeground : highlighted ? Theme.MenuSelectionForeground : Theme.MenuForeground;

			painter.DrawText(font, entry.Label!, left + padX, baseline, color);

			if (entry.Children is not null) painter.DrawIcon(Codicon.ChevronRight, left + width - padX - (6f * scale), cursor + (rowHeight / 2f), 14f * scale, color);
			else if (KeybindingOf(entry) is { } keybinding) painter.DrawText(font, keybinding, left + width - padX - font.Measure(keybinding), baseline, Theme.DescriptionForeground);

			cursor += rowHeight;
		}

		painter.PopRoundedClip();
		painter.StrokeRoundedRectangle(left, top, width, height, radius, Metrics.StrokeThickness * scale, Theme.MenuBorder);

		return new Panel(left, top, width, height);
	}
}
