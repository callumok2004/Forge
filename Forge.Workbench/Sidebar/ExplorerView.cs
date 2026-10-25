using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Common.Workspace;
using Forge.Git;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ExplorerView : Panel
{
	private readonly List<FileNode> _visible = [];
	private readonly ListPanel _list;

	private IFileTree? _tree;

	public ExplorerView() {
		Name = "Explorer";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_list = new ListPanel { Name = "ExplorerTree", Dock = DockType.Fill, RowHeight = Metrics.TreeRowHeight, RowPainter = PaintRow, Activated = Activate };
		AddChild(_list);
	}

	public IGitRepository? Repository { get; set; }

	public Action<string>? FileOpened { get; set; }

	public string? SelectedPath {
		get;
		set {
			if (field == value) return;

			field = value;
			SyncSelection();
		}
	}

	public void SetTree(IFileTree tree) {
		_tree = tree;
		Rebuild();
	}

	public void Reload(IEnumerable<string> directories) {
		if (_tree is not { } tree) return;

		bool changed = false;

		foreach (string directory in directories) changed |= tree.Refresh(directory);

		if (changed) Rebuild();

		_list.Repaint();
	}

	private void Rebuild() {
		_tree?.Flatten(_visible);
		_list.SetCount(_visible.Count);
		SyncSelection();
	}

	private void SyncSelection() {
		for (int i = 0; i < _visible.Count; i++) {
			if (_visible[i].IsDirectory || _visible[i].FullPath != SelectedPath) continue;

			_list.Select(i);
			return;
		}

		_list.ClearSelection();
	}

	private void Activate(int row) {
		if (row < 0 || row >= _visible.Count) return;

		FileNode node = _visible[row];

		if (node.IsDirectory) {
			_tree?.Toggle(node);
			Rebuild();
			return;
		}

		SelectedPath = node.FullPath;
		FileOpened?.Invoke(node.FullPath);
	}

	private void PaintRow(IPainter painter, int row, float left, float top, float width, float height, bool selected) {
		FileNode node = _visible[row];
		ITextFont font = painter.GetUi();
		float scale = Scale;
		float indent = (Metrics.Space8 * scale) + (node.Depth * Metrics.Space12 * scale);
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float centreY = top + (height / 2f);
		float iconSize = Metrics.IconSize * scale;

		GitFileStatus status = Repository is { } repository ? node.IsDirectory ? repository.StatusOfDirectory(node.FullPath) : repository.StatusOf(node.FullPath) : GitFileStatus.Unchanged;
		Color color = GitStatusColor.Of(status) ?? (selected ? Theme.TabActiveForeground : node.IsDirectory ? Theme.ChromeForeground : Theme.SidebarFileForeground);

		float twistieCentre = left + indent + (iconSize / 2f);
		float iconX = left + indent + iconSize + (iconSize / 2f);

		if (node.IsDirectory) {
			painter.DrawIcon(node.IsExpanded ? Codicon.ChevronDown : Codicon.ChevronRight, twistieCentre, centreY, iconSize, Theme.SectionHeaderForeground);

			if (!painter.DrawFolderIcon(node.Name, node.IsExpanded, iconX, centreY, iconSize)) painter.DrawIcon(node.IsExpanded ? Codicon.FolderOpened : Codicon.Folder, iconX, centreY, iconSize, color);
		}
		else if (!painter.DrawFileIcon(node.Name, iconX, centreY, iconSize)) painter.DrawIcon(FileIcon(node.Name), iconX, centreY, iconSize, FileTypeColor(node.Name));

		string letter = node.IsDirectory ? string.Empty : GitStatusLetter.Of(status);
		float letterWidth = letter.Length == 0 ? 0f : font.Measure(letter) + (Metrics.Space8 * scale);
		float textX = left + indent + (iconSize * 2f) + (Metrics.Space4 * scale);

		painter.PushClip(left, top, Math.Max(0f, width - (Metrics.SpaceSm * scale) - letterWidth), height);
		painter.DrawText(font, node.Name, textX, baseline, color);
		painter.PopClip();

		if (letter.Length > 0) painter.DrawText(font, letter, left + width - (Metrics.Space8 * scale) - font.Measure(letter), baseline, color);
	}

	private static Color FileTypeColor(string name) {
		ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());

		if (Is(extension, ".cs") || Is(extension, ".csx")) return Color.Parse("#B392F0");

		if (Is(extension, ".json") || Is(extension, ".jsonc")) return Color.Parse("#FFAB70");

		if (Is(extension, ".md")) return Color.Parse("#79B8FF");

		if (Is(extension, ".csproj") || Is(extension, ".props") || Is(extension, ".targets") || Is(extension, ".slnx") || Is(extension, ".xml") || Is(extension, ".config")) return Color.Parse("#6BD68A");

		if (Is(extension, ".ts") || Is(extension, ".tsx") || Is(extension, ".js") || Is(extension, ".jsx")) return Color.Parse("#F9E27D");

		return Theme.SectionHeaderForeground;
	}

	private static Codicon FileIcon(string name) {
		ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());

		if (Is(extension, ".json") || Is(extension, ".jsonc")) return Codicon.Json;

		return Is(extension, ".md") ? Codicon.Markdown : Codicon.File;
	}

	private static bool Is(ReadOnlySpan<char> extension, string value) => extension.Equals(value, StringComparison.OrdinalIgnoreCase);
}
