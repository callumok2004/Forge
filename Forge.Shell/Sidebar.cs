using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.Workspace;
using Forge.Git;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.UI;
using Forge.Workspace;

namespace Forge.Shell;

public sealed class Sidebar
{
	private readonly List<FileNode> _visible = [];

	private IFileTree? _tree;
	private float _scrollY;
	private int _hoverRow = -1;

	private float _listHeight;

	public float Width { get; set; } = Metrics.SidebarDefaultWidth;

	public string? SelectedPath { get; set; }

	public string Title { get; set; } = "EXPLORER";

	public IGitRepository? Repository { get; set; }

	public ActivityView View { get; set; } = ActivityView.Explorer;

	public SourceControlView SourceControl { get; } = new();

	public float ContentLeft { get; private set; }

	public float ContentWidth { get; private set; }

	public void SetTree(IFileTree tree) {
		_tree = tree;
		Rebuild();
	}

	private void Rebuild() => _tree?.Flatten(_visible);

	public void Resize(float width) =>
		Width = Math.Clamp(width, Metrics.SidebarMinWidth, Metrics.SidebarMaxWidth);

	public void ScrollByWheel(int wheelDelta, float scale) {
		float rowHeight = Metrics.TreeRowHeight * scale;
		_scrollY -= (wheelDelta / 120f) * 3f * rowHeight;
		ClampScroll(rowHeight);
	}

	private void ClampScroll(float rowHeight) {
		float contentHeight = _visible.Count * rowHeight;
		_scrollY = Math.Clamp(_scrollY, 0f, Math.Max(0f, contentHeight - _listHeight));
	}

	public int RowAt(float x, float y, float left, float top, float scale) {
		float headerHeight = Metrics.SidebarHeaderHeight * scale;

		float outer = Metrics.CardMargin * scale;
		float inner = Metrics.CardInnerMargin * scale;
		left += outer;

		if (x < left || x > left + (Width * scale) - outer - inner || y < top + headerHeight) return -1;

		int row = (int)((y - top - headerHeight + _scrollY) / (Metrics.TreeRowHeight * scale));
		return row >= 0 && row < _visible.Count ? row : -1;
	}

	public FileNode? NodeAt(int row) => row >= 0 && row < _visible.Count ? _visible[row] : null;

	public bool SetHover(int row) {
		bool changed = _hoverRow != row;
		_hoverRow = row;
		return changed;
	}

	public string? Activate(int row) {
		FileNode? node = NodeAt(row);
		if (node is null) return null;

		if (node.IsDirectory) {
			_tree?.Toggle(node);
			Rebuild();
			return null;
		}

		SelectedPath = node.FullPath;
		return node.FullPath;
	}

	public void Render(IPainter painter, float x, float y, float height, float scale) {
		ITextFont font = painter.GetUi();
		float width = Width * scale;
		float rowHeight = Metrics.TreeRowHeight * scale;
		float headerHeight = Metrics.SidebarHeaderHeight * scale;

		float outer = Metrics.CardMargin * scale;
		float inner = Metrics.CardInnerMargin * scale;

		float cardX = x + outer;
		float cardWidth = Math.Max(0f, width - outer - inner);
		float cardHeight = Math.Max(0f, height - inner);
		float radius = Metrics.RadiusLarge * scale;

		painter.FillRoundedRectangle(cardX, y, cardWidth, cardHeight, radius, Theme.ChromeBackground);
		painter.StrokeRoundedRectangle(cardX, y, cardWidth, cardHeight, radius,
			Metrics.StrokeThickness * scale, Theme.PanelBorder);

		x = cardX;
		width = cardWidth;
		height = cardHeight;

		ContentLeft = x;
		ContentWidth = width;

		if (View != ActivityView.Explorer) {
			painter.PushClip(x, y, width, height);
			SourceControl.Render(painter, Repository, x, y, width, height, scale);
			painter.PopClip();
			return;
		}

		ITextFont titleFont = painter.GetUiStrong();
		float headerBaseline = y + ((headerHeight - titleFont.GetLineHeight()) / 2f) + titleFont.GetBaseline();

		painter.DrawText(titleFont, Title, x + ((Metrics.Space8 + Metrics.Space4) * scale), headerBaseline,
			Theme.SidebarTitleForeground);

		float listTop = y + headerHeight;
		float listHeight = height - headerHeight;

		_listHeight = listHeight;
		ClampScroll(rowHeight);

		painter.PushClip(x, listTop, width, listHeight);

		int firstRow = Math.Max(0, (int)(_scrollY / rowHeight));
		int lastRow = Math.Min(_visible.Count - 1, firstRow + (int)(listHeight / rowHeight) + 1);
		float offset = _scrollY - (firstRow * rowHeight);

		for (int row = firstRow; row <= lastRow; row++) {
			FileNode node = _visible[row];
			float rowTop = listTop + ((row - firstRow) * rowHeight) - offset;
			bool isSelected = !node.IsDirectory && node.FullPath == SelectedPath;

			float inset = Metrics.RowInset * scale;
			float chipRadius = Metrics.RadiusSmall * scale;

			if (isSelected) {
				painter.FillRoundedRectangle(x + inset, rowTop, width - (inset * 2f), rowHeight, chipRadius,
					Theme.ListActiveSelectionBackground);
			}
			else if (row == _hoverRow) {
				painter.FillRoundedRectangle(x + inset, rowTop, width - (inset * 2f), rowHeight, chipRadius,
					Theme.ListHoverBackground);
			}

			float indent = (Metrics.Space8 * scale) + (node.Depth * Metrics.Space12 * scale);
			float baseline = rowTop + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float centreY = rowTop + (rowHeight / 2f);

			GitFileStatus status = Repository is { } repository
				? node.IsDirectory
					? repository.StatusOfDirectory(node.FullPath)
					: repository.StatusOf(node.FullPath)
				: GitFileStatus.Unchanged;

			Color color = GitStatusColor.Of(status) ?? (isSelected
				? Theme.TabActiveForeground
				: node.IsDirectory ? Theme.ChromeForeground : Theme.SidebarFileForeground);

			float iconSize = Metrics.IconSize * scale;

			float twistieCentre = x + indent + (iconSize / 2f);
			float iconX = x + indent + iconSize + (iconSize / 2f);

			if (node.IsDirectory) {
				painter.DrawIcon(node.IsExpanded ? Codicon.ChevronDown : Codicon.ChevronRight,
					twistieCentre, centreY, iconSize, Theme.SectionHeaderForeground);

				if (!painter.DrawFolderIcon(node.Name, node.IsExpanded, iconX, centreY, iconSize)) {
					painter.DrawIcon(node.IsExpanded ? Codicon.FolderOpened : Codicon.Folder, iconX, centreY, iconSize, color);
				}
			}
			else if (!painter.DrawFileIcon(node.Name, iconX, centreY, iconSize)) {
				painter.DrawIcon(FileIcon(node.Name), iconX, centreY, iconSize, FileTypeColor(node.Name));
			}

			string letter = node.IsDirectory ? string.Empty : GitStatusLetter.Of(status);
			float letterWidth = letter.Length == 0 ? 0f : font.Measure(letter) + (Metrics.Space8 * scale);

			float textX = x + indent + (iconSize * 2f) + (Metrics.Space4 * scale);
			painter.PushClip(x, rowTop, Math.Max(0f, width - (Metrics.SpaceSm * scale) - letterWidth),
				rowHeight);
			painter.DrawText(font, node.Name, textX, baseline, color);
			painter.PopClip();

			if (letter.Length > 0) {
				painter.DrawText(font, letter,
					x + width - (Metrics.Space8 * scale) - font.Measure(letter), baseline, color);
			}
		}

		painter.PopClip();
	}

	private static Color FileTypeColor(string name) {
		ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());

		if (Is(extension, ".cs") || Is(extension, ".csx")) return Color.Parse("#B392F0");

		if (Is(extension, ".json") || Is(extension, ".jsonc")) return Color.Parse("#FFAB70");

		if (Is(extension, ".md")) return Color.Parse("#79B8FF");

		if (Is(extension, ".csproj") || Is(extension, ".props") || Is(extension, ".targets") ||
			Is(extension, ".slnx") || Is(extension, ".xml") || Is(extension, ".config")) {
			return Color.Parse("#6BD68A");
		}

		if (Is(extension, ".ts") || Is(extension, ".tsx") || Is(extension, ".js") || Is(extension, ".jsx")) {
			return Color.Parse("#F9E27D");
		}

		return Theme.SectionHeaderForeground;
	}

	private static Codicon FileIcon(string name) {
		ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());

		if (Is(extension, ".json") || Is(extension, ".jsonc")) return Codicon.Json;

		if (Is(extension, ".md")) return Codicon.Markdown;

		return Codicon.File;
	}

	private static bool Is(ReadOnlySpan<char> extension, string value) =>
		extension.Equals(value, StringComparison.OrdinalIgnoreCase);
}
