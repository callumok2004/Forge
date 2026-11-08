using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Common.Workspace;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed partial class ExplorerView
{
	public const string FocusContext = "filesExplorerFocus";

	private enum EditMode
	{
		None,
		NewFile,
		NewFolder,
		Rename,
	}

	private readonly HashSet<string> _cut = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly List<string> _clipboard = [];

	private TextEntry _edit = null!;
	private EditMode _mode;
	private FileNode? _editTarget;
	private FileNode? _placeholder;
	private string? _compareSource;

	public string? RootPath { get; set; }

	public Action<string>? RevealRequested { get; set; }

	public Action<string>? TerminalRequested { get; set; }

	public Action<string>? FindInFolderRequested { get; set; }

	public Action<string, string>? CompareRequested { get; set; }

	public Action<string, string>? Renamed { get; set; }

	public Action<string>? Deleted { get; set; }

	private void InitializeActions() {
		_edit = new TextEntry { Name = "ExplorerEdit", Visible = false, Submitted = null };
		_edit.TextChanged = ValidateEdit;
		AddChild(_edit);
	}

	private FileNode? _contextNode;

	public bool IsEditing => _mode != EditMode.None;

	private FileNode? Target => _contextNode ?? (_list.Selected >= 0 && _list.Selected < _visible.Count ? _visible[_list.Selected] : null);

	private string? TargetPath => Target?.FullPath ?? RootPath;

	private string? TargetFolder => Target is { } node ? node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath) : RootPath;

	private void OpenContextMenu(int row, float x, float y) {
		_contextNode = row >= 0 && row < _visible.Count && !ReferenceEquals(_visible[row], _placeholder) ? _visible[row] : null;

		if (_contextNode is null) _list.ClearSelection();

		Menu menu = new();
		FileNode? node = _contextNode;
		bool file = node is { IsDirectory: false };

		if (node is null || node.IsDirectory) {
			Add(menu, "New File...", "explorer.newFile", NewFile);
			Add(menu, "New Folder...", "explorer.newFolder", NewFolder);
		}

		Add(menu, "Reveal in File Explorer", "revealFileInOS", RevealInOS);
		Add(menu, "Open in Integrated Terminal", "openInIntegratedTerminal", OpenInTerminal);

		if (file) {
			menu.AddSeparator();
			Add(menu, "Select for Compare", "selectForCompare", SelectForCompare);

			if (_compareSource is { } source && !source.Equals(node!.FullPath, StringComparison.OrdinalIgnoreCase)) Add(menu, "Compare with Selected", "compareFiles", CompareWithSelected);
		}

		if (node is null || node.IsDirectory) {
			menu.AddSeparator();
			Add(menu, "Find in Folder...", "filesExplorer.findInFolder", FindInFolder);
		}

		menu.AddSeparator();

		if (node is not null) {
			Add(menu, "Cut", "filesExplorer.cut", Cut);
			Add(menu, "Copy", "filesExplorer.copy", Copy);
		}

		if (_clipboard.Count > 0 && (node is null || node.IsDirectory)) Add(menu, "Paste", "filesExplorer.paste", Paste);

		if (node is not null || _clipboard.Count > 0) menu.AddSeparator();

		Add(menu, "Copy Path", "copyFilePath", CopyPath);
		Add(menu, "Copy Relative Path", "copyRelativeFilePath", CopyRelativePath);

		if (node is not null) {
			menu.AddSeparator();
			Add(menu, "Rename...", "renameFile", Rename);
			Add(menu, "Delete", "deleteFile", Delete);
		}

		menu.Closed = () => _contextNode = null;

		(float absX, float absY) = _list.GetAbsPos();
		menu.Open(absX + x, absY + y);
	}

	private void Add(Menu menu, string label, string command, Action action) {
		FileNode? node = _contextNode;
		menu.Add(new MenuItem(label, command, () => {
			_contextNode = node;
			action();
			_contextNode = null;
		}));
	}

	#region Commands

	public void NewFile() => BeginCreate(EditMode.NewFile);

	public void NewFolder() => BeginCreate(EditMode.NewFolder);

	private void BeginCreate(EditMode mode) {
		if (_tree is null || TargetFolder is not { } folder) return;

		CancelEdit();

		FileNode? parent = FindVisible(folder);

		if (parent is { IsExpanded: false }) {
			_tree.Toggle(parent);
			_tree.Flatten(_visible);
		}

		_mode = mode;
		_editTarget = parent;
		_placeholder = new FileNode(Path.Combine(folder, "\u0000"), string.Empty, mode == EditMode.NewFolder, parent is null ? 0 : parent.Depth + 1);
		Rebuild();
		ShowEdit(string.Empty);
	}

	public void Rename() {
		if (Target is not { IsGroup: false } node) return;

		CancelEdit();
		_mode = EditMode.Rename;
		_editTarget = node;
		ShowEdit(node.Name);
	}

	public void Delete() {
		if (Target is not { IsGroup: false } node) return;

		string path = node.FullPath;
		string message = node.IsDirectory ? $"Are you sure you want to delete '{node.Name}' and its contents?" : $"Are you sure you want to delete '{node.Name}'?";

		ConfirmDialog.Show(message, "You can restore this file from the Recycle Bin.", "Move to Recycle Bin", () => {
			if (!RecycleBin.Send(path)) return;

			Deleted?.Invoke(path);
			Reload([Path.GetDirectoryName(path) ?? path]);
		}, Codicon.Warning);
	}

	public void Cut() {
		if (TargetPath is not { } path || Target is null) return;

		_clipboard.Clear();
		_clipboard.Add(path);
		_cut.Clear();
		_cut.Add(path);
		_list.Repaint();
	}

	public void Copy() {
		if (TargetPath is not { } path || Target is null) return;

		_clipboard.Clear();
		_clipboard.Add(path);
		_cut.Clear();
		_list.Repaint();
	}

	public void Paste() {
		if (_clipboard.Count == 0 || TargetFolder is not { } folder) return;

		bool move = _cut.Count > 0;
		List<string> touched = [folder];

		foreach (string source in _clipboard) {
			bool directory = Directory.Exists(source);

			if (!directory && !File.Exists(source)) continue;

			string name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
			string destination = Path.Combine(folder, name);

			if (move && destination.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;

			if (!move || File.Exists(destination) || Directory.Exists(destination)) destination = IncrementName(folder, name, directory);

			try {
				if (move) {
					if (directory) Directory.Move(source, destination);
					else File.Move(source, destination);

					Renamed?.Invoke(source, destination);
					touched.Add(Path.GetDirectoryName(source) ?? source);
				}
				else if (directory) CopyDirectory(source, destination);
				else File.Copy(source, destination);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			}
		}

		if (move) {
			_clipboard.Clear();
			_cut.Clear();
		}

		Reload(touched);
	}

	private static string IncrementName(string folder, string name, bool directory) {
		string stem = directory ? name : Path.GetFileNameWithoutExtension(name);
		string extension = directory ? string.Empty : Path.GetExtension(name);

		for (int copy = 1; ; copy++) {
			string candidate = Path.Combine(folder, stem + (copy == 1 ? " copy" : " copy " + copy) + extension);

			if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
		}
	}

	private static void CopyDirectory(string source, string destination) {
		Directory.CreateDirectory(destination);

		foreach (string file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));

		foreach (string directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
	}

	public void CopyPath() {
		if (TargetPath is { } path) Clipboard.SetText(path);
	}

	public void CopyRelativePath() {
		if (TargetPath is not { } path) return;

		Clipboard.SetText(RootPath is { } root ? Path.GetRelativePath(root, path) : path);
	}

	public void RevealInOS() {
		if (TargetPath is { } path) RevealRequested?.Invoke(path);
	}

	public void OpenInTerminal() {
		if (TargetFolder is { } folder) TerminalRequested?.Invoke(folder);
	}

	public void FindInFolder() {
		if (TargetFolder is { } folder) FindInFolderRequested?.Invoke(folder);
	}

	public void SelectForCompare() {
		if (Target is { IsDirectory: false } node) _compareSource = node.FullPath;
	}

	public void CompareWithSelected() {
		if (_compareSource is not { } source || Target is not { IsDirectory: false } node) return;

		CompareRequested?.Invoke(source, node.FullPath);
	}

	#endregion

	#region Inline edit

	private FileNode? FindVisible(string path) {
		foreach (FileNode node in _visible) {
			if (node.IsDirectory && node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return node;
		}

		return null;
	}

	private void InsertPlaceholder() {
		if (_placeholder is null) return;

		int index = _editTarget is null ? 0 : _visible.IndexOf(_editTarget) + 1;

		if (_editTarget is not null && index == 0) {
			CancelEdit();
			return;
		}

		_visible.Insert(index, _placeholder);
	}

	private int EditRow => _mode == EditMode.Rename ? _editTarget is null ? -1 : _visible.IndexOf(_editTarget) : _placeholder is null ? -1 : _visible.IndexOf(_placeholder);

	private void ShowEdit(string text) {
		int row = EditRow;

		if (row < 0) {
			CancelEdit();
			return;
		}

		_list.Select(row);
		_list.Reveal(row);
		_edit.SetText(text);
		_edit.SelectAll();
		_edit.Invalid = false;
		_edit.Visible = true;
		LayoutEdit();
		_edit.RequestFocus();
		_edit.Submitted = CommitEdit;
	}

	private void LayoutEdit() {
		int row = EditRow;

		if (!_edit.Visible || row < 0) return;

		FileNode node = _visible[row];
		float scale = Scale;
		float iconSize = Metrics.IconSize * scale;
		float left = (Metrics.Space8 * scale) + (node.Depth * Metrics.Space12 * scale) + (iconSize * 2f) + (Metrics.Space2 * scale);
		float height = Metrics.TreeRowHeight * scale;
		_edit.SetBounds(_list.X + left, _list.Y + _list.RowTop(row), Math.Max(0f, _list.Wide - left - (Metrics.Space4 * scale)), height);
	}

	public override void PerformLayout() {
		base.PerformLayout();
		LayoutEdit();
	}

	private string? EditDestination(out string? error) {
		error = null;
		string name = _edit.GetString().Trim();

		if (name.Length == 0) {
			error = "A file or folder name must be provided.";
			return null;
		}

		if (name.IndexOfAny(Path.GetInvalidFileNameChars().Where(static c => c is not '\\' and not '/').ToArray()) >= 0) {
			error = $"The name **{name}** is not valid as a file or folder name. Please choose a different name.";
			return null;
		}

		string folder = _mode == EditMode.Rename ? Path.GetDirectoryName(_editTarget!.FullPath)! : _editTarget?.FullPath ?? RootPath ?? string.Empty;
		string destination = Path.GetFullPath(Path.Combine(folder, name));

		bool same = _mode == EditMode.Rename && destination.Equals(_editTarget!.FullPath, StringComparison.Ordinal);

		if (!same && (File.Exists(destination) || Directory.Exists(destination)) && !(_mode == EditMode.Rename && destination.Equals(_editTarget!.FullPath, StringComparison.OrdinalIgnoreCase))) {
			error = $"A file or folder **{name}** already exists at this location. Please choose a different name.";
			return null;
		}

		return destination;
	}

	private void ValidateEdit() {
		if (!_edit.Visible) return;

		EditDestination(out string? error);
		_edit.Invalid = error is not null && _edit.GetString().Trim().Length > 0;
	}

	private void CommitEdit() {
		if (_mode == EditMode.None) return;

		EditMode mode = _mode;
		FileNode? target = _editTarget;
		string? destination = EditDestination(out _);

		CancelEdit();

		if (destination is null) return;

		try {
			switch (mode) {
				case EditMode.NewFile:
					Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
					File.WriteAllBytes(destination, []);
					Reload([Path.GetDirectoryName(destination)!]);
					SelectedPath = destination;
					FileOpened?.Invoke(destination);
					FilePinned?.Invoke(destination);
					break;

				case EditMode.NewFolder:
					Directory.CreateDirectory(destination);
					Reload([Path.GetDirectoryName(destination)!]);
					break;

				case EditMode.Rename when target is not null && !destination.Equals(target.FullPath, StringComparison.Ordinal):
					if (target.IsDirectory) Directory.Move(target.FullPath, destination);
					else File.Move(target.FullPath, destination);

					Renamed?.Invoke(target.FullPath, destination);
					Reload([Path.GetDirectoryName(destination)!]);

					if (!target.IsDirectory) SelectedPath = destination;

					break;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private void CancelEdit() {
		bool rebuild = _placeholder is not null;

		_mode = EditMode.None;
		_placeholder = null;
		_editTarget = null;
		_edit.Submitted = null;
		_edit.Visible = false;

		if (rebuild) Rebuild();
	}

	private void PaintPlaceholder(IPainter painter, FileNode node, float left, float top, float height) {
		float scale = Scale;
		float iconSize = Metrics.IconSize * scale;
		float indent = (Metrics.Space8 * scale) + (node.Depth * Metrics.Space12 * scale);
		float iconX = left + indent + iconSize + (iconSize / 2f);
		float centreY = top + (height / 2f);

		if (node.IsDirectory) painter.DrawIcon(Codicon.Folder, iconX, centreY, iconSize, Theme.SidebarFileForeground);
		else painter.DrawIcon(Codicon.File, iconX, centreY, iconSize, Theme.SidebarFileForeground);
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (_edit.HasFocus) {
			if (key == VirtualKey.Escape) {
				CancelEdit();
				_list.RequestFocus();
				return true;
			}

			return false;
		}

		switch (key) {
			case VirtualKey.Escape when _cut.Count > 0:
				_cut.Clear();
				_clipboard.Clear();
				_list.Repaint();
				return true;
		}

		return base.OnKeyCodePressed(key, modifiers);
	}

	#endregion
}
