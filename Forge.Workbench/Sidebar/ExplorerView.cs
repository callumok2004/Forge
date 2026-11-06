using Forge.Common.Commands;
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

	private readonly Dictionary<string, (int Errors, int Warnings)> _fileProblems = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, DiagnosticSeverity> _folderProblems = new(StringComparer.OrdinalIgnoreCase);

	private const float BadgeScale = 0.9f;
	private const float BadgeOpacity = 0.75f;
	private const float BadgeGap = 5f;
	private const float BadgeMarginRight = 16f;
	private const float BubbleSize = 14f;
	private const float BubbleMarginRight = 14f;
	private const float BubbleOpacity = 0.4f;

	public void SetProblems(IReadOnlyList<Problem> problems, string? root) {
		_fileProblems.Clear();
		_folderProblems.Clear();

		foreach (Problem problem in problems) {
			if (problem.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning) || string.IsNullOrEmpty(problem.File)) continue;

			bool error = problem.Severity == DiagnosticSeverity.Error;
			_fileProblems.TryGetValue(problem.File, out (int Errors, int Warnings) counts);
			_fileProblems[problem.File] = error ? (counts.Errors + 1, counts.Warnings) : (counts.Errors, counts.Warnings + 1);

			ReadOnlySpan<char> directory = Path.GetDirectoryName(problem.File.AsSpan());

			while (!directory.IsEmpty) {
				string key = directory.ToString();

				if (_folderProblems.TryGetValue(key, out DiagnosticSeverity existing) && (existing == DiagnosticSeverity.Error || !error)) break;

				_folderProblems[key] = error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;

				if (root is not null && directory.Equals(root.AsSpan().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;

				directory = Path.GetDirectoryName(directory);
			}
		}

		_list.Repaint();
	}

	private DiagnosticSeverity? ProblemsOf(FileNode node) {
		if (node.IsGroup) {
			DiagnosticSeverity? worst = null;

			if (node.Children is null) return null;

			foreach (FileNode child in node.Children) {
				DiagnosticSeverity? severity = ProblemsOf(child);

				if (severity == DiagnosticSeverity.Error) return severity;

				worst ??= severity;
			}

			return worst;
		}

		if (node.IsDirectory) return _folderProblems.TryGetValue(node.FullPath, out DiagnosticSeverity severity) ? severity : null;

		if (!_fileProblems.TryGetValue(node.FullPath, out (int Errors, int Warnings) counts)) return null;

		return counts.Errors > 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
	}

	private GitFileStatus StatusOf(FileNode node) {
		if (Repository is not { } repository) return GitFileStatus.Unchanged;

		if (!node.IsGroup) return node.IsDirectory ? repository.StatusOfDirectory(node.FullPath) : repository.StatusOf(node.FullPath);

		if (node.Children is null) return GitFileStatus.Unchanged;

		foreach (FileNode child in node.Children) {
			GitFileStatus status = StatusOf(child);

			if (status is not (GitFileStatus.Unchanged or GitFileStatus.Ignored)) return status;
		}

		return GitFileStatus.Unchanged;
	}

	public ExplorerView() {
		Name = "Explorer";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_list = new ListPanel { Name = "ExplorerTree", Dock = DockType.Fill, RowHeight = Metrics.TreeRowHeight, PaddingRows = 1, RowPainter = PaintRow, Activated = Activate };
		AddChild(_list);
	}

	public IGitRepository? Repository { get; set; }

	public Action<string>? FileOpened { get; set; }

	public string? SelectedPath {
		get;
		set {
			if (field == value) return;

			field = value;
			_selectedNode = null;
			SyncSelection();
		}
	}

	private IReadOnlyList<ExplorerGroup>? _groups;

	public void SetTree(IFileTree tree) {
		_tree = tree;

		if (_groups is not null) tree.SetGroups(_groups);

		Rebuild();
	}

	public void CaptureExpanded(List<string> destination) => _tree?.CaptureExpanded(destination);

	public void SetGroups(IReadOnlyList<ExplorerGroup>? groups) {
		_groups = groups;

		if (_tree is not { } tree) return;

		tree.SetGroups(groups);
		Rebuild();
	}

	public void CollapseAll() {
		if (_tree is not { } tree) return;

		tree.CollapseAll();
		Rebuild();
	}

	public void RestoreExpanded(IReadOnlyList<string> paths) {
		if (_tree is not { } tree) return;

		tree.RestoreExpanded(paths);
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
		SyncSelection(reveal: false);
	}

	private FileNode? _selectedNode;

	private void SyncSelection(bool reveal = true) {
		for (int i = 0; i < _visible.Count; i++) {
			if (_selectedNode is { } node ? !ReferenceEquals(_visible[i], node) : _visible[i].IsDirectory || _visible[i].FullPath != SelectedPath) continue;

			_list.Select(i, reveal);
			return;
		}

		_list.ClearSelection();
	}

	private void Activate(int row) {
		if (row < 0 || row >= _visible.Count) return;

		FileNode node = _visible[row];

		if (node.IsDirectory) {
			_selectedNode = node;
			_tree?.Toggle(node);
			Rebuild();
			return;
		}

		_selectedNode = null;
		SelectedPath = node.FullPath;
		_selectedNode = node;
		FileOpened?.Invoke(node.FullPath);
	}

	private static readonly Color GroupIconForeground = Color.Parse("#C09553");

	private void PaintRow(IPainter painter, int row, float left, float top, float width, float height, bool selected) {
		FileNode node = _visible[row];
		ITextFont font = painter.GetUi();
		float scale = Scale;
		float indent = (Metrics.Space8 * scale) + (node.Depth * Metrics.Space12 * scale);
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float centreY = top + (height / 2f);
		float iconSize = Metrics.IconSize * scale;

		GitFileStatus status = StatusOf(node);
		DiagnosticSeverity? problems = ProblemsOf(node);
		Color? decoration = problems switch {
			DiagnosticSeverity.Error => Theme.ErrorForeground,
			DiagnosticSeverity.Warning => Theme.WarningForeground,
			_ => GitStatusColor.Of(status),
		};
		Color color = decoration ?? (selected ? Theme.ListActiveSelectionForeground : Theme.SidebarFileForeground);

		float twistieCentre = left + indent + (iconSize / 2f);
		float iconX = left + indent + iconSize + (iconSize / 2f);

		if (node.IsDirectory) {
			painter.DrawIcon(node.IsExpanded ? Codicon.ChevronDown : Codicon.ChevronRight, twistieCentre, centreY, iconSize, Theme.SectionHeaderForeground);

			if (node.IsGroup) painter.DrawIcon(node.IsExpanded ? Codicon.FolderOpened : Codicon.Folder, iconX, centreY, iconSize, GroupIconForeground);
			else if (!painter.DrawFolderIcon(node.Name, node.IsExpanded, iconX, centreY, iconSize)) painter.DrawIcon(node.IsExpanded ? Codicon.FolderOpened : Codicon.Folder, iconX, centreY, iconSize, color);
		}
		else if (!painter.DrawFileIcon(node.Name, iconX, centreY, iconSize)) painter.DrawIcon(FileIcon(node.Name), iconX, centreY, iconSize, FileTypeColor(node.Name));

		float textX = left + indent + (iconSize * 2f) + (Metrics.Space4 * scale);
		float right = left + width;

		if (node.IsDirectory) {
			if (decoration is { } bubble) {
				float bubbleSize = BubbleSize * scale;
				right -= (BubbleMarginRight * scale) + bubbleSize;
				painter.DrawIcon(Codicon.CircleFilled, right + (bubbleSize / 2f), centreY, bubbleSize, bubble.WithAlpha(BubbleOpacity));
			}
		}
		else {
			ITextFont badge = Ui!.GetFonts().GetUi(Metrics.FontSizeBody * BadgeScale, true);
			float badgeBaseline = top + ((height - badge.GetLineHeight()) / 2f) + badge.GetBaseline();
			float gap = BadgeGap * scale;
			right -= BadgeMarginRight * scale;
			string letter = GitStatusLetter.Of(status);

			if (letter.Length > 0) right = DrawBadge(painter, badge, letter, right, badgeBaseline, GitStatusColor.Of(status) ?? color) - gap;

			if (_fileProblems.TryGetValue(node.FullPath, out (int Errors, int Warnings) counts)) {
				if (counts.Warnings > 0) right = DrawBadge(painter, badge, Count(counts.Warnings), right, badgeBaseline, Theme.WarningForeground) - gap;

				if (counts.Errors > 0) right = DrawBadge(painter, badge, Count(counts.Errors), right, badgeBaseline, Theme.ErrorForeground) - gap;
			}
		}

		painter.PushClip(left, top, Math.Max(0f, right - left - (Metrics.SpaceSm * scale)), height);
		painter.DrawText(node.IsGroup ? painter.GetUiStrong() : font, node.Name, textX, baseline, color);
		painter.PopClip();
	}

	private static string Count(int value) => value switch {
		1 => "1", 2 => "2", 3 => "3", 4 => "4", 5 => "5", 6 => "6", 7 => "7", 8 => "8", 9 => "9",
		_ => "9+",
	};

	private static float DrawBadge(IPainter painter, ITextFont font, string text, float right, float baseline, Color color) {
		float x = right - font.Measure(text);
		painter.DrawText(font, text, x, baseline, color.WithAlpha(BadgeOpacity));
		return x;
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
