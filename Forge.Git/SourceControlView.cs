using System.Text;

using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Git;

public enum SourceControlHit
{
	None,

	Message,

	Commit,

	File,

	Stage,

	Unstage,

	Discard,

	Twistie,

	TitleCommit,

	TitleRefresh,

	TitleMore,

	StageAll,

	UnstageAll,

	DiscardAll,

	Sync,
}

public sealed class SourceControlView
{
	private const float MessageHeight = 26f;

	private const float MessageRowHeight = MessageHeight + 10f;

	private const float ButtonHeight = 28f;

	private const float ButtonRowHeight = ButtonHeight + 8f;

	private const float PaddingX = 4f;

	private const float ActionSize = 22f;

	private const float DecorationWidth = 16f;

	private const float DecorationMargin = 5f;

	private const float Indent = Metrics.Space12;

	private readonly StringBuilder _message = new();

	private readonly List<Row> _rows = [];

	private readonly List<GitEntry> _sorted = [];

	private readonly List<float> _actionRight = [];

	public bool TreeView { get; set; } = true;

	public GitMenu Menu { get; } = new();

	private readonly HashSet<string> _collapsed = [with(StringComparer.OrdinalIgnoreCase)];

	private string? _statePath;

	private string? _twistieKey;

	public string? TwistieKey => _twistieKey;

	private string? _groupTitle;

	public string? GroupTitle => _groupTitle;

	public void LoadState(string path) {
		_statePath = path;

		try {
			if (File.Exists(path)) {
				foreach (string line in File.ReadAllLines(path)) {
					if (line.Length > 0) _collapsed.Add(line);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private void SaveState() {
		if (_statePath is null) return;

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
			File.WriteAllLines(_statePath, _collapsed);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private bool IsCollapsed(string key) => _collapsed.Contains(key);

	public void Toggle(string key) {
		if (!_collapsed.Remove(key)) _collapsed.Add(key);

		SaveState();
	}

	private float _scrollY;
	private int _hoverRow = -1;
	private SourceControlHit _hoverAction;

	private SourceControlHit _pressedAction;

	public void SetPressed(SourceControlHit action) => _pressedAction = action;

	public SourceControlHit Pressed => _pressedAction;

	private float _left;
	private float _width;
	private float _titleTop;
	private float _headerTop;
	private float _repositoryTop;

	private float _repositoryActionRight;

	private float _scale = 1f;
	private float _messageTop;
	private float _buttonTop;
	private float _listTop;
	private float _listHeight;

	private readonly record struct Row(GitEntry Entry, float Top, bool Staged, RowKind Kind, string? Title,
		int Depth, string? Key);

	private enum RowKind
	{
		Group,
		Folder,
		File,
	}

	public bool MessageFocused { get; private set; }

	public string Message => _message.ToString();

	public void FocusMessage(bool focused) => MessageFocused = focused;

	public void Append(char character) {
		_message.Append(character);
		Caret.Reset();
	}

	public void Backspace() {
		if (_message.Length > 0) _message.Length--;

		Caret.Reset();
	}

	public void ClearMessage() => _message.Clear();

	public void ScrollByWheel(int wheelDelta, float scale) {
		float rowHeight = Metrics.TreeRowHeight * scale;
		_scrollY = Math.Max(0f, _scrollY - ((wheelDelta / 120f) * 3f * rowHeight));
	}

	public bool SetHover(int row, SourceControlHit action) {
		bool changed = _hoverRow != row || _hoverAction != action;

		_hoverRow = row;
		_hoverAction = action;

		return changed;
	}

	public void OpenMenu() =>
		Menu.Open(_left + _width - (PaddingX * _scale), _titleTop + (Metrics.TitleHeight * _scale));

	#region Hit testing

	public SourceControlHit HitTest(float x, float y, float scale, out GitEntry entry, out bool staged) {
		entry = default;
		staged = false;

		float actionSize = ActionSize * scale;
		float viewRight = _left + _width - (PaddingX * scale);

		if (y >= _titleTop && y < _headerTop) {
			return x >= viewRight - actionSize ? SourceControlHit.TitleMore : SourceControlHit.None;
		}

		if (y >= _headerTop && y < _repositoryTop) return SourceControlHit.None;

		if (y >= _repositoryTop && y < _messageTop) {
			float repoRight = _repositoryActionRight > 0f ? _repositoryActionRight : viewRight;

			if (x >= repoRight - actionSize) return SourceControlHit.TitleRefresh;

			if (x >= repoRight - (actionSize * 2f)) return SourceControlHit.TitleCommit;

			if (x >= repoRight - (actionSize * 3f)) return SourceControlHit.Sync;

			return SourceControlHit.None;
		}

		if (y >= _messageTop && y < _messageTop + (MessageRowHeight * scale)) return SourceControlHit.Message;

		if (y >= _buttonTop && y < _buttonTop + (ButtonRowHeight * scale)) return SourceControlHit.Commit;

		int index = RowAt(y, scale);

		if (index < 0) return SourceControlHit.None;

		Row row = _rows[index];

		if (row.Kind == RowKind.Group) {
			_twistieKey = row.Key;
			_groupTitle = row.Title;
			staged = row.Staged;

			float groupSize = ActionSize * scale;
			float groupRight = index < _actionRight.Count && _actionRight[index] > 0f
				? _actionRight[index]
				: _left + _width - (PaddingX * scale);

			if (x >= groupRight - groupSize) {
				return row.Staged ? SourceControlHit.UnstageAll : SourceControlHit.StageAll;
			}

			if (!row.Staged && x >= groupRight - (groupSize * 2f)) return SourceControlHit.DiscardAll;

			return SourceControlHit.Twistie;
		}

		if (row.Kind == RowKind.Folder) {
			_twistieKey = row.Key;
			return SourceControlHit.Twistie;
		}

		entry = row.Entry;
		staged = row.Staged;

		float size = ActionSize * scale;
		float right = _left + _width - (PaddingX * scale);

		if (x >= right - size) return staged ? SourceControlHit.Unstage : SourceControlHit.Stage;

		if (!staged && x >= right - (size * 2f)) return SourceControlHit.Discard;

		return SourceControlHit.File;
	}

	public int RowAt(float y, float scale) {
		float rowHeight = Metrics.TreeRowHeight * scale;

		for (int i = 0; i < _rows.Count; i++) {
			if (y >= _rows[i].Top && y < _rows[i].Top + rowHeight) return i;
		}

		return -1;
	}

	#endregion

	public void Render(IPainter painter, IGitRepository? repository, float x, float y, float width,
		float height, float scale) {
		_left = x;
		_width = width;

		ITextFont font = painter.GetUi();
		float padding = PaddingX * scale;

		_titleTop = y;
		_scale = scale;
		DrawTitle(painter, x, y, width, scale);

		float cursor = y + (Metrics.TitleHeight * scale);

		if (repository is null) {
			painter.DrawText(font, "This folder is not a git repository.", x + padding,
				cursor + (Metrics.Space12 * scale) + font.GetBaseline(), Theme.SectionHeaderForeground);

			_rows.Clear();
			return;
		}

		float rowHeight = Metrics.TreeRowHeight * scale;

		_headerTop = cursor;
		DrawViewHeader(painter, x, cursor, width, rowHeight, scale);
		cursor += rowHeight;

		_repositoryTop = cursor;
		DrawRepositoryRow(painter, repository, x, cursor, width, rowHeight, scale);
		cursor += rowHeight;

		_messageTop = cursor;
		DrawMessageBox(painter, x, cursor + (5f * scale), width, scale);

		cursor += MessageRowHeight * scale;

		_buttonTop = cursor;
		DrawCommitButton(painter, repository, x, cursor + (4f * scale), width, scale);

		cursor += ButtonRowHeight * scale;

		_listTop = cursor;
		_listHeight = Math.Max(0f, height - (cursor - y));

		LayoutRows(repository, scale);
		DrawRows(painter, x, width, scale);
	}

	private void DrawTitle(IPainter painter, float x, float y, float width, float scale) {
		ITextFont font = painter.GetUiStrong();
		float height = Metrics.TitleHeight * scale;
		float baseline = y + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.DrawText(font, "Source Control", x + ((Metrics.Space8 + Metrics.Space4) * scale),
			baseline, Theme.SidebarTitleForeground);

		float size = ActionSize * scale;

		DrawAction(painter, Codicon.Ellipsis, SourceControlHit.TitleMore,
			x + width - (PaddingX * scale) - (size / 2f), y + (height / 2f), size, scale);
	}

	private static void DrawViewHeader(IPainter painter, float x, float y, float width, float rowHeight, float scale) {
		ITextFont font = painter.GetUi();
		float centreY = y + (rowHeight / 2f);
		float content = ContentLeft(x, 0, scale);
		float iconSize = Metrics.IconSize * scale;

		painter.DrawIcon(Codicon.ChevronDown, content + (iconSize / 2f), centreY, iconSize,
			Theme.SectionHeaderForeground);

		painter.DrawText(font, "Changes", content + iconSize + (Metrics.Space4 * scale),
			y + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline(), Theme.SidebarTitleForeground);
	}

	private void DrawRepositoryRow(IPainter painter, IGitRepository repository, float x, float y, float width,
		float rowHeight, float scale) {
		ITextFont font = painter.GetUi();
		float centreY = y + (rowHeight / 2f);
		float content = ContentLeft(x, 1, scale);
		float iconSize = Metrics.IconSize * scale;
		float baseline = y + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.DrawIcon(Codicon.ChevronDown, content + (iconSize / 2f), centreY, iconSize,
			Theme.SectionHeaderForeground);

		float cursor = content + iconSize + (Metrics.Space4 * scale);

		painter.DrawIcon(Codicon.Repo, cursor + (iconSize / 2f), centreY, iconSize, Theme.ChromeForeground);
		cursor += iconSize + (Metrics.Space4 * scale);

		string name = Path.GetFileName(repository.GetRootPath());

		painter.DrawText(painter.GetUiStrong(), name, cursor, baseline, Theme.SidebarTitleForeground);
		cursor += painter.GetUiStrong().Measure(name) + (Metrics.Space8 * scale);

		int count = repository.GetStaged().Count + repository.GetUnstaged().Count + repository.GetMerge().Count;
		float badgeWidth = BadgeWidth(painter, count, scale);
		float badgeLeft = x + width - (PaddingX * scale) - badgeWidth;

		DrawCountBadge(painter, count, badgeLeft, centreY, scale);

		float size = ActionSize * scale;
		float right = badgeLeft - (Metrics.Space4 * scale);

		_repositoryActionRight = right;

		DrawAction(painter, Codicon.Refresh, SourceControlHit.TitleRefresh,
			right - (size / 2f), centreY, size, scale);

		DrawAction(painter, Codicon.Check, SourceControlHit.TitleCommit,
			right - size - (size / 2f), centreY, size, scale);

		DrawAction(painter, Codicon.Sync, SourceControlHit.Sync,
			right - (size * 2f) - (size / 2f), centreY, size, scale);

		if (repository.GetBranch() is { } branch) {
			painter.PushClip(cursor, y, Math.Max(0f, right - (size * 4f) - cursor), rowHeight);

			painter.DrawIcon(Codicon.GitBranch, cursor + (iconSize / 2f), centreY, iconSize,
				Theme.SectionHeaderForeground);

			painter.DrawText(font, branch, cursor + iconSize + (Metrics.Space4 * scale), baseline,
				Theme.SectionHeaderForeground);

			painter.PopClip();
		}
	}

	private static float ContentLeft(float x, int level, float scale) =>
		x + (Metrics.Space8 * scale) + (level * Indent * scale);

	private void DrawMessageBox(IPainter painter, float x, float y, float width, float scale) {
		float boxLeft = ContentLeft(x, 2, scale);
		float boxWidth = Math.Max(0f, x + width - (PaddingX * scale) - boxLeft);

		TextBox.Draw(painter, _message.ToString(), "Message", boxLeft, y, boxWidth,
			MessageHeight * scale, scale, MessageFocused);
	}

	private void DrawCommitButton(IPainter painter, IGitRepository repository, float x, float y,
		float width, float scale) {
		float buttonLeft = ContentLeft(x, 2, scale);
		float buttonWidth = Math.Max(0f, x + width - (PaddingX * scale) - buttonLeft);

		bool enabled = repository.GetStaged().Count > 0 || repository.GetUnstaged().Count > 0;

		Button.Draw(painter, "Commit", Codicon.Check, buttonLeft, y, buttonWidth,
			ButtonHeight * scale, scale, enabled, _hoverAction == SourceControlHit.Commit,
			_pressedAction == SourceControlHit.Commit);
	}

	private void LayoutRows(IGitRepository repository, float scale) {
		_rows.Clear();
		_actionRight.Clear();

		float rowHeight = Metrics.TreeRowHeight * scale;
		float cursor = _listTop - _scrollY;

		cursor = AddGroup(repository.GetMerge(), "Merge Changes", staged: false, cursor, rowHeight);
		cursor = AddGroup(repository.GetStaged(), "Staged Changes", staged: true, cursor, rowHeight);
		cursor = AddGroup(repository.GetUnstaged(), "Changes", staged: false, cursor, rowHeight);

		float content = cursor - (_listTop - _scrollY);
		_scrollY = Math.Clamp(_scrollY, 0f, Math.Max(0f, content - _listHeight));
	}

	private float AddGroup(IReadOnlyList<GitEntry> entries, string title, bool staged, float cursor,
		float rowHeight) {
		if (entries.Count == 0) return cursor;

		string groupKey = $"group:{title}";

		_rows.Add(new Row(default, cursor, staged, RowKind.Group, title, entries.Count, groupKey));
		cursor += rowHeight;

		if (IsCollapsed(groupKey)) return cursor;

		if (!TreeView) {
			foreach (GitEntry entry in entries) {
				_rows.Add(new Row(entry, cursor, staged, RowKind.File, null, 0, null));
				cursor += rowHeight;
			}

			return cursor;
		}

		_sorted.Clear();
		_sorted.AddRange(entries);
		_sorted.Sort(static (left, right) =>
			string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));

		string previous = string.Empty;

		string? hiddenUnder = null;

		foreach (GitEntry entry in _sorted) {
			string folder = TrimRoot(Path.GetDirectoryName(entry.Path) ?? string.Empty);

			if (hiddenUnder is not null &&
				folder.StartsWith(hiddenUnder, StringComparison.OrdinalIgnoreCase)) continue;

			hiddenUnder = null;

			if (!string.Equals(folder, previous, StringComparison.OrdinalIgnoreCase)) {
				int shared = SharedSegments(previous, folder);
				string[] segments = folder.Length == 0
					? []
					: folder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

				for (int depth = shared; depth < segments.Length; depth++) {
					string path = string.Join(Path.DirectorySeparatorChar, segments[..(depth + 1)]);
					string key = $"folder:{groupKey}:{path}";

					_rows.Add(new Row(default, cursor, staged, RowKind.Folder, segments[depth], depth, key));
					cursor += rowHeight;

					if (IsCollapsed(key)) {
						hiddenUnder = path;
						break;
					}
				}

				previous = folder;
			}

			if (hiddenUnder is not null) continue;

			int fileDepth = folder.Length == 0
				? 0
				: folder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

			_rows.Add(new Row(entry, cursor, staged, RowKind.File, null, fileDepth, null));
			cursor += rowHeight;
		}

		return cursor;
	}

	private static int SharedSegments(string left, string right) {
		if (left.Length == 0 || right.Length == 0) return 0;

		string[] a = left.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
		string[] b = right.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

		int shared = 0;
		while (shared < a.Length && shared < b.Length &&
			string.Equals(a[shared], b[shared], StringComparison.OrdinalIgnoreCase)) shared++;

		return shared;
	}

	private void DrawRows(IPainter painter, float x, float width, float scale) {
		ITextFont font = painter.GetUi();
		ITextFont groupFont = painter.GetUiStrong();
		float rowHeight = Metrics.TreeRowHeight * scale;
		float padding = PaddingX * scale;
		float iconSize = Metrics.IconSize * scale;

		painter.PushClip(x, _listTop, width, _listHeight);

		for (int i = 0; i < _rows.Count; i++) {
			Row row = _rows[i];

			if (row.Top + rowHeight < _listTop || row.Top > _listTop + _listHeight) continue;

			float baseline = row.Top + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float centreY = row.Top + (rowHeight / 2f);

			if (row.Kind == RowKind.Group) {
				if (i == _hoverRow) {
					painter.FillRoundedRectangle(x + (Metrics.Space4 * scale), row.Top,
						width - (Metrics.Space8 * scale), rowHeight, Metrics.RadiusSmall * scale,
						Theme.ListHoverBackground);
				}

				float groupLeft = ContentLeft(x, 1, scale);

				painter.DrawIcon(
					row.Key is { } key && IsCollapsed(key) ? Codicon.ChevronRight : Codicon.ChevronDown,
					groupLeft + (iconSize / 2f), centreY, iconSize, Theme.SectionHeaderForeground);

				painter.DrawText(groupFont, row.Title!, groupLeft + iconSize + (Metrics.Space4 * scale),
					row.Top + ((rowHeight - groupFont.GetLineHeight()) / 2f) + groupFont.GetBaseline(),
					Theme.SidebarTitleForeground);

				float badgeWidth = BadgeWidth(painter, row.Depth, scale);
				float badgeLeft = x + width - padding - badgeWidth;

				DrawCountBadge(painter, row.Depth, badgeLeft, centreY, scale);

				float actionsRight = badgeLeft - (Metrics.Space4 * scale);

				while (_actionRight.Count <= i) _actionRight.Add(0f);

				_actionRight[i] = actionsRight;

				if (i == _hoverRow) {
					float size = ActionSize * scale;

					DrawAction(painter, row.Staged ? Codicon.Remove : Codicon.Add,
						row.Staged ? SourceControlHit.UnstageAll : SourceControlHit.StageAll,
						actionsRight - (size / 2f), centreY, size, scale);

					if (!row.Staged) {
						DrawAction(painter, Codicon.Discard, SourceControlHit.DiscardAll,
							actionsRight - size - (size / 2f), centreY, size, scale);
					}
				}

				continue;
			}

			float rowLeft = ContentLeft(x, 2 + row.Depth, scale);

			for (int level = 1; level < 2 + row.Depth; level++) {
				painter.FillRectangle(ContentLeft(x, level, scale) + (iconSize / 2f), row.Top,
					Metrics.StrokeThickness * scale, rowHeight, Theme.WidgetBorder);
			}

			if (i == _hoverRow) {
				painter.FillRoundedRectangle(x + (Metrics.Space4 * scale), row.Top,
					width - (Metrics.Space8 * scale), rowHeight, Metrics.RadiusSmall * scale,
					Theme.ListHoverBackground);
			}

			if (row.Kind == RowKind.Folder) {
				float folderX = rowLeft;

				painter.DrawIcon(
					row.Key is { } folderKey && IsCollapsed(folderKey)
						? Codicon.ChevronRight
						: Codicon.ChevronDown,
					folderX + (iconSize / 2f), centreY, iconSize, Theme.SectionHeaderForeground);

				painter.DrawFolderIcon(row.Title!, expanded: true, folderX + iconSize + (iconSize / 2f), centreY, iconSize);

				painter.DrawText(font, row.Title!, folderX + (iconSize * 2f) + (Metrics.Space4 * scale),
					baseline, Theme.ChromeForeground);

				continue;
			}

			Color color = GitStatusColor.Of(row.Entry.Status) ?? Theme.SidebarFileForeground;

			string name = Path.GetFileName(row.Entry.Path);

			string? folder = TreeView ? null : Path.GetDirectoryName(row.Entry.Path);

			float iconX = rowLeft + iconSize;
			float textX = iconX + iconSize + (Metrics.Space4 * scale);

			painter.DrawFileIcon(name, iconX + (iconSize / 2f), centreY, iconSize);

			float actionsWidth = (row.Staged ? 1f : 2f) * ActionSize * scale;

			painter.PushClip(x, row.Top, Math.Max(0f, width - padding - actionsWidth), rowHeight);

			painter.DrawText(font, name, textX, baseline, color);

			if (folder is { Length: > 0 }) {
				painter.DrawText(font, TrimRoot(folder), textX + font.Measure(name) + (Metrics.Space8 * scale),
					baseline, Theme.GitIgnoredForeground);
			}

			painter.PopClip();

			DrawRowActions(painter, row, i, x, width, centreY, scale, color);
		}

		painter.PopClip();
	}

	private static float BadgeWidth(IPainter painter, int count, float scale) =>
		Badge.Width(painter.GetUiSmall(), count, scale);

	private static void DrawCountBadge(IPainter painter, int count, float left, float centreY, float scale) =>
		Badge.Draw(painter, count, left, centreY, scale, Theme.ListActiveSelectionBackground,
			Theme.EditorForeground);

	private void DrawRowActions(IPainter painter, Row row, int index, float x, float width, float centreY,
		float scale, Color statusColor) {
		float size = ActionSize * scale;
		float right = x + width - (PaddingX * scale);
		bool hovered = index == _hoverRow;

		if (!hovered) {
			string letter = GitStatusLetter.Of(row.Entry.Status);

			if (letter.Length > 0) {
				ITextFont font = painter.GetUi();
				float slot = DecorationWidth * scale;

				painter.DrawText(font, letter, right - slot + ((slot - font.Measure(letter)) / 2f),
					centreY - (font.GetLineHeight() / 2f) + font.GetBaseline(), statusColor);
			}

			return;
		}

		DrawAction(painter, row.Staged ? Codicon.Remove : Codicon.Add,
			row.Staged ? SourceControlHit.Unstage : SourceControlHit.Stage,
			right - (size / 2f), centreY, size, scale);

		if (!row.Staged) {
			DrawAction(painter, Codicon.Discard, SourceControlHit.Discard,
				right - size - (size / 2f), centreY, size, scale);
		}
	}

	private void DrawAction(IPainter painter, Codicon icon, SourceControlHit action, float centreX,
		float centreY, float size, float scale) =>
		IconButton.Draw(painter, icon, centreX, centreY, scale, _hoverAction == action,
			_pressedAction == action, Theme.ChromeForeground);

	private string _rootPath = string.Empty;

	public void SetRoot(string rootPath) => _rootPath = rootPath;

	private string TrimRoot(string folder) =>
		_rootPath.Length > 0 && folder.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase)
			? folder[_rootPath.Length..].TrimStart(Path.DirectorySeparatorChar)
			: folder;
}
