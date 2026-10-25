using Forge.UI.Controls;

namespace Forge.Workbench;

public readonly record struct MenuEntry(string? Label, string? CommandId = null, MenuEntry[]? Children = null)
{
	public bool IsSeparator => Label is null;

	public static MenuEntry Separator => new(null);

	public static MenuEntry Submenu(string label, MenuEntry[] children) => new(label, Children: children);
}

public static class ApplicationMenu
{
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

	public static Menu Build(MenuEntry[] entries) {
		Menu menu = new();

		foreach (MenuEntry entry in entries)
			menu.Add(entry.IsSeparator ? MenuItem.Separator() : new MenuItem(entry.Label, entry.CommandId, submenu: entry.Children is { } children ? Build(children) : null));

		return menu;
	}
}
