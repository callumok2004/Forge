using Forge.Common.Render;
using Forge.Core;
using Forge.Theming;

namespace Forge.UI;

public enum MenuCommand
{
	None,

	OpenFile,
	OpenFolder,
	Save,
	SaveAs,
	CloseEditor,
	OpenSettings,
	Exit,

	Undo,
	Redo,
	Cut,
	Copy,
	Paste,

	SelectAll,

	ToggleSidebar,
	ToggleAuxiliaryBar,
	TogglePanel,
	ShowTerminal,
	ShowProblems,
	ShowOutput,
	ToggleMinimap,
	ToggleFrameStats,
	ZoomIn,
	ZoomOut,
	ZoomReset,

	GoToFile,

	ShowCommands,

	RunCommand,
}

public readonly record struct MenuEntry(string? Label, MenuCommand Command, string? Keybinding = null,
	string? CommandId = null)
{
	public bool IsSeparator => Label is null;

	public static MenuEntry Separator => new(null, MenuCommand.None);
}

public sealed class MenuPopup
{
	private const float RowHeight = 22f;
	private const float SeparatorHeight = 7f;
	private const float PaddingY = 4f;
	private const float PaddingX = 10f;

	private const float KeybindingGap = 28f;

	private static readonly MenuEntry[][] Menus =
	[
		[
			new("Open File…", MenuCommand.OpenFile, "Ctrl+O"),
			new("Open Folder…", MenuCommand.OpenFolder, "Ctrl+K Ctrl+O"),
			MenuEntry.Separator,
			new("Save", MenuCommand.Save, "Ctrl+S"),
			new("Save As…", MenuCommand.SaveAs, "Ctrl+Shift+S"),
			MenuEntry.Separator,
			new("Settings", MenuCommand.OpenSettings, "Ctrl+,"),
			MenuEntry.Separator,
			new("Close Editor", MenuCommand.CloseEditor, "Ctrl+W"),
			new("Exit", MenuCommand.Exit, "Alt+F4"),
		],

		[
			new("Undo", MenuCommand.Undo, "Ctrl+Z"),
			new("Redo", MenuCommand.Redo, "Ctrl+Y"),
			MenuEntry.Separator,
			new("Cut", MenuCommand.Cut, "Ctrl+X"),
			new("Copy", MenuCommand.Copy, "Ctrl+C"),
			new("Paste", MenuCommand.Paste, "Ctrl+V"),
		],

		[
			new("Select All", MenuCommand.SelectAll, "Ctrl+A"),
		],

		[
			new("Toggle Primary Sidebar", MenuCommand.ToggleSidebar, "Ctrl+B"),
			new("Toggle Secondary Side Bar", MenuCommand.ToggleAuxiliaryBar, "Ctrl+Alt+B"),
			new("Toggle Panel", MenuCommand.TogglePanel, "Ctrl+J"),
			new("Toggle Minimap", MenuCommand.ToggleMinimap),
			MenuEntry.Separator,
			new("Terminal", MenuCommand.ShowTerminal, "Ctrl+`"),
			new("Problems", MenuCommand.ShowProblems, "Ctrl+Shift+M"),
			new("Output", MenuCommand.ShowOutput, "Ctrl+Shift+U"),
			new("Toggle Frame Statistics", MenuCommand.ToggleFrameStats, "F12"),
			MenuEntry.Separator,
			new("Zoom In", MenuCommand.ZoomIn, "Ctrl+="),
			new("Zoom Out", MenuCommand.ZoomOut, "Ctrl+-"),
			new("Reset Zoom", MenuCommand.ZoomReset, "Ctrl+0"),
		],

		[
			new("Go to File…", MenuCommand.GoToFile, "Ctrl+P"),
		],

		[
			new("Show All Commands", MenuCommand.ShowCommands, "Ctrl+Shift+P"),
		],

		[
			new("Forge", MenuCommand.None),
		],
	];

	private int _menuIndex = -1;
	private int _selected = -1;
	private float _left;
	private float _top;
	private float _width;
	private float _height;

	public bool IsOpen => _menuIndex >= 0;

	public int MenuIndex => _menuIndex;

	public void Open(int menuIndex) {
		_menuIndex = menuIndex >= 0 && menuIndex < Menus.Length ? menuIndex : -1;
		_selected = -1;
	}

	public void Close() {
		_menuIndex = -1;
		_selected = -1;
	}

	public void Cycle(int delta) {
		if (!IsOpen) return;

		Open(((_menuIndex + delta) % Menus.Length + Menus.Length) % Menus.Length);
	}

	public void MoveSelection(int delta) {
		if (!IsOpen) return;

		MenuEntry[] entries = Menus[_menuIndex];
		int index = _selected;

		for (int step = 0; step < entries.Length; step++) {
			index = ((index + delta) % entries.Length + entries.Length) % entries.Length;

			if (!entries[index].IsSeparator) {
				_selected = index;
				return;
			}
		}
	}

	public MenuCommand Accept() {
		if (!IsOpen || _selected < 0 || _selected >= Menus[_menuIndex].Length) return MenuCommand.None;

		MenuEntry entry = Menus[_menuIndex][_selected];
		CommandId = entry.CommandId;

		return entry.Command;
	}

	public bool Hover(float x, float y) {
		int row = RowAt(x, y);

		if (row != _selected && (row >= 0 || Contains(x, y))) _selected = row;

		return Contains(x, y);
	}

	public PixelRect Bounds => IsOpen
		? PixelRect.Around(_left - 2f, _top - 2f, _width + 4f, _height + 4f)
		: PixelRect.Empty;

	public bool Contains(float x, float y) =>
		IsOpen && x >= _left && x < _left + _width && y >= _top && y < _top + _height;

	public MenuCommand CommandAt(float x, float y) {
		int row = RowAt(x, y);

		CommandId = row >= 0 ? Menus[_menuIndex][row].CommandId : null;

		return row >= 0 ? Menus[_menuIndex][row].Command : MenuCommand.None;
	}

	public string? CommandId { get; private set; }

	private int RowAt(float x, float y) {
		if (!Contains(x, y)) return -1;

		MenuEntry[] entries = Menus[_menuIndex];
		float cursor = _top + (PaddingY * _renderScale);

		for (int i = 0; i < entries.Length; i++) {
			float height = (entries[i].IsSeparator ? SeparatorHeight : RowHeight) * _renderScale;

			if (!entries[i].IsSeparator && y >= cursor && y < cursor + height) return i;

			cursor += height;
		}

		return -1;
	}

	private float _renderScale = 1f;

	public void Render(IPainter painter, float titleLeft, float top, float windowWidth, float scale) {
		if (!IsOpen) return;

		_renderScale = scale;

		ITextFont font = painter.GetUi();
		MenuEntry[] entries = Menus[_menuIndex];

		float padX = PaddingX * scale;
		float padY = PaddingY * scale;

		float widest = 0f;
		float content = 0f;

		foreach (MenuEntry entry in entries) {
			content += (entry.IsSeparator ? SeparatorHeight : RowHeight) * scale;

			if (entry.IsSeparator) continue;

			float row = font.Measure(entry.Label!) +
				(entry.Keybinding is null ? 0f : (KeybindingGap * scale) + font.Measure(entry.Keybinding));

			widest = Math.Max(widest, row);
		}

		_width = widest + (padX * 2f);
		_height = content + (padY * 2f);

		_left = Math.Max(0f, Math.Min(titleLeft, windowWidth - _width));
		_top = top;

		float radius = Metrics.RadiusLarge * scale;

		painter.PushRoundedClip(_left, _top, _width, _height, radius);
		painter.FillRoundedRectangle(_left, _top, _width, _height, radius, Theme.WidgetBackground);

		float cursor = _top + padY;

		for (int i = 0; i < entries.Length; i++) {
			MenuEntry entry = entries[i];

			if (entry.IsSeparator) {
				float ruleY = cursor + ((SeparatorHeight * scale) / 2f);
				painter.DrawHorizontalLine(ruleY, _left + padX, _left + _width - padX, Theme.WidgetBorder);
				cursor += SeparatorHeight * scale;
				continue;
			}

			float rowHeight = RowHeight * scale;

			if (i == _selected) {
				float inset = Metrics.Space4 * scale;
				painter.FillRoundedRectangle(_left + inset, cursor, _width - (inset * 2f), rowHeight,
					Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);
			}

			float baseline = cursor + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

			Color color = entry.Command == MenuCommand.None
				? Theme.SectionHeaderForeground
				: i == _selected ? Theme.TabActiveForeground : Theme.EditorForeground;

			painter.DrawText(font, entry.Label!, _left + padX, baseline, color);

			if (entry.Keybinding is { } keybinding) {
				float keyX = _left + _width - padX - font.Measure(keybinding);
				painter.DrawText(font, keybinding, keyX, baseline, Theme.SectionHeaderForeground);
			}

			cursor += rowHeight;
		}

		painter.PopRoundedClip();

		painter.StrokeRoundedRectangle(_left, _top, _width, _height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}
}
