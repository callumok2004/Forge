using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Git;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ScmTree : Panel
{
	private const float RowHeight = 22f;
	private const float InputRowHeight = 32f;
	private const float InputHeight = 26f;
	private const float ButtonRowHeight = 32f;
	private const float ButtonHeight = 26f;
	private const float Indent = 8f;
	private const float Twistie = 16f;
	private const float TwistiePadding = 6f;
	private const float WidgetPadding = 11f;
	private const float ContentPaddingRight = 12f;
	private const float ActionSize = 20f;
	private const float DecorationWidth = 16f;
	private const float DecorationMargin = 5f;
	private const float IconGap = 6f;

	private readonly SourceControlView _view;
	private readonly TextEntry _input;
	private readonly SplitButton _commit;
	private readonly List<ScmRow> _rows = [];
	private readonly List<GitEntry> _sorted = [];

	private volatile bool _stale = true;
	private float _scrollY;
	private int _hover = -1;
	private int _hoverAction = -1;
	private int _pressed = -1;
	private int _pressedAction = -1;
	private string? _selectedKey;

	public ScmTree(SourceControlView view) {
		_view = view;
		Name = "ScmTree";
		Dock = DockType.Fill;

		_input = new TextEntry { Name = "ScmInput", ContextKey = ContextKeys.ScmInputFocused };
		_commit = new SplitButton { Name = "ScmCommit", Command = "git.commit", DropdownMenu = static () => ApplicationMenu.Build(GitMenus.Commit) };

		AddChild(_input);
		AddChild(_commit);
	}

	public TextEntry Input => _input;

	public string Message => _input.GetString();

	public void Invalidate() {
		_stale = true;
		InvalidateLayout();
	}

	private float HeaderHeight => (InputRowHeight + ButtonRowHeight) * Scale;

	private void EnsureRows() {
		if (!_stale) return;

		_stale = false;
		_rows.Clear();

		if (_view.Repository is not { } repository) return;

		AddGroup(repository.GetMerge(), ScmGroup.Merge, "Merge Changes", ScmRowAction.MergeGroup);
		AddGroup(repository.GetStaged(), ScmGroup.Staged, "Staged Changes", ScmRowAction.StagedGroup);
		AddGroup(repository.GetUnstaged(), ScmGroup.Changes, "Changes", ScmRowAction.ChangesGroup);

		SyncButton(repository);
		ClampScroll();
	}

	private void SyncButton(IGitRepository repository) {
		Span<char> buffer = stackalloc char[96];
		int written = Write(buffer, "$(check) Commit");
		_commit.SetText(buffer[..written]);
		_commit.Enabled = repository.GetStaged().Count > 0 || repository.GetUnstaged().Count > 0;

		string branch = repository.GetBranch() ?? "HEAD";
		written = Write(buffer, "Message (Ctrl+Enter to commit on '");
		written += Write(buffer[written..], branch.AsSpan(0, Math.Min(branch.Length, buffer.Length - written - 2)));
		written += Write(buffer[written..], "')");
		_input.Placeholder = new string(buffer[..written]);
	}

	private static int Write(Span<char> destination, ReadOnlySpan<char> text) {
		text.CopyTo(destination);
		return text.Length;
	}

	private void AddGroup(IReadOnlyList<GitEntry> entries, ScmGroup group, string title, ScmRowAction[] actions) {
		if (entries.Count == 0) return;

		string groupKey = $"group:{title}";
		_rows.Add(new ScmRow(ScmRowKind.Group, group, 1, title, groupKey, default, entries.Count, actions));

		if (_view.IsCollapsed(groupKey)) return;

		bool staged = group == ScmGroup.Staged;
		ScmRowAction[] fileActions = staged ? ScmRowAction.StagedFile : ScmRowAction.ChangedFile;

		if (!_view.TreeView) {
			foreach (GitEntry entry in entries) _rows.Add(new ScmRow(ScmRowKind.File, group, 2, Path.GetFileName(entry.Path), entry.Path, entry, 0, fileActions));

			return;
		}

		_sorted.Clear();
		_sorted.AddRange(entries);
		_sorted.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));

		ScmRowAction[] folderActions = staged ? ScmRowAction.StagedFolder : ScmRowAction.ChangedFolder;
		string root = _view.RootPath;
		string previous = string.Empty;
		string? hiddenUnder = null;

		foreach (GitEntry entry in _sorted) {
			string folder = _view.TrimRoot(Path.GetDirectoryName(entry.Path) ?? string.Empty);

			if (hiddenUnder is not null && (folder.Equals(hiddenUnder, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(hiddenUnder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;

			hiddenUnder = null;

			string[] segments = folder.Length == 0 ? [] : folder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

			if (!folder.Equals(previous, StringComparison.OrdinalIgnoreCase)) {
				for (int depth = SharedSegments(previous, folder); depth < segments.Length; depth++) {
					string relative = string.Join(Path.DirectorySeparatorChar, segments[..(depth + 1)]);
					string key = $"folder:{groupKey}:{relative}";

					_rows.Add(new ScmRow(ScmRowKind.Folder, group, 2 + depth, segments[depth], key, new GitEntry(Path.Combine(root, relative), GitFileStatus.Unchanged), 0, folderActions));

					if (!_view.IsCollapsed(key)) continue;

					hiddenUnder = relative;
					break;
				}

				previous = folder;
			}

			if (hiddenUnder is not null) {
				previous = string.Empty;
				continue;
			}

			_rows.Add(new ScmRow(ScmRowKind.File, group, 2 + segments.Length, Path.GetFileName(entry.Path), entry.Path, entry, 0, fileActions));
		}
	}

	private static int SharedSegments(string left, string right) {
		if (left.Length == 0 || right.Length == 0) return 0;

		string[] a = left.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
		string[] b = right.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
		int shared = 0;

		while (shared < a.Length && shared < b.Length && a[shared].Equals(b[shared], StringComparison.OrdinalIgnoreCase)) shared++;

		return shared;
	}

	#region Row actions

	public void Open(in ScmRow row) => _view.FileOpened?.Invoke(row.Entry.Path);

	public void Stage(ScmRow row) => _view.Run(repository => repository.Stage(row.Entry.Path));

	public void Unstage(ScmRow row) => _view.Run(repository => repository.Unstage(row.Entry.Path));

	public void Discard(ScmRow row) {
		string name = row.Kind == ScmRowKind.Folder ? row.Label : Path.GetFileName(row.Entry.Path);

		if (!Win32.Confirm($"Are you sure you want to discard changes in '{name}'?", "This action is irreversible!")) return;

		_view.Run(repository => repository.Discard(row.Entry.Path, row.Entry.Status == GitFileStatus.Untracked));
	}

	#endregion

	#region Layout and scrolling

	private float ContentHeight => HeaderHeight + (_rows.Count * RowHeight * Scale);

	private void ClampScroll() => _scrollY = Math.Clamp(_scrollY, 0f, Math.Max(0f, ContentHeight - Tall));

	public override void PerformLayout() {
		EnsureRows();

		float scale = Scale;
		float left = (Indent + WidgetPadding) * scale;
		float width = Math.Max(0f, Wide - left - (ContentPaddingRight * scale));

		_input.SetBounds(left, ((InputRowHeight - InputHeight) / 2f * scale) - _scrollY, width, InputHeight * scale);
		_commit.SetBounds(left, ((InputRowHeight + ((ButtonRowHeight - ButtonHeight) / 2f)) * scale) - _scrollY, width, ButtonHeight * scale);
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float previous = _scrollY;
		_scrollY -= delta / 120f * 3f * RowHeight * Scale;
		ClampScroll();

		if (_scrollY != previous) {
			InvalidateLayout();
			Repaint();
		}

		return true;
	}

	#endregion

	#region Input

	private int RowAt(float y) {
		float top = HeaderHeight - _scrollY;

		if (y < top) return -1;

		int row = (int)((y - top) / (RowHeight * Scale));
		return row < _rows.Count ? row : -1;
	}

	private int ActionAt(int row, float x) {
		if (row < 0) return -1;

		float scale = Scale;
		ScmRowAction[] actions = _rows[row].Actions;
		float right = ActionsRight(_rows[row], scale);

		for (int i = actions.Length - 1; i >= 0; i--) {
			float left = right - (ActionSize * scale);

			if (x >= left && x < right) return i;

			right = left;
		}

		return -1;
	}

	private float ActionsRight(in ScmRow row, float scale) {
		float right = Wide - (ContentPaddingRight * scale);

		if (row.Kind == ScmRowKind.Group) return right - BadgeWidth(row.Count, scale) - (Metrics.Space4 * scale);

		return row.Kind == ScmRowKind.File ? right - ((DecorationWidth + DecorationMargin) * scale) : right;
	}

	private float BadgeWidth(int count, float scale) => Ui is null ? 0f : Badge.Width(Ui.GetFonts().GetUiSmall(), count, scale);

	public override void OnCursorMoved(float x, float y) {
		EnsureRows();

		int row = RowAt(y);
		int action = ActionAt(row, x);

		if (row == _hover && action == _hoverAction) return;

		RepaintRow(_hover);
		_hover = row;
		_hoverAction = action;
		RepaintRow(_hover);
	}

	public override void OnCursorExited() {
		RepaintRow(_hover);
		_hover = -1;
		_hoverAction = -1;
		_pressed = -1;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		EnsureRows();

		_pressed = RowAt(y);
		_pressedAction = ActionAt(_pressed, x);

		if (_pressed >= 0 && _pressedAction < 0) {
			_selectedKey = _rows[_pressed].Key;
			Repaint();
		}

		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		int row = RowAt(y);
		int action = ActionAt(row, x);
		int pressed = _pressed;
		int pressedAction = _pressedAction;

		_pressed = -1;
		_pressedAction = -1;

		if (row < 0 || row != pressed || action != pressedAction) return true;

		ScmRow target = _rows[row];

		if (action >= 0) {
			target.Actions[action].Run(this, target);
			return true;
		}

		if (target.Kind == ScmRowKind.File) Open(target);
		else Toggle(target.Key);

		return true;
	}

	private void Toggle(string key) {
		_view.Toggle(key);
		Invalidate();
		Repaint();
	}

	private void RepaintRow(int row) {
		if (row < 0) return;

		float height = RowHeight * Scale;
		Repaint(0f, HeaderHeight - _scrollY + (row * height), Wide, height);
	}

	#endregion

	#region Painting

	public override void PaintBackground(IPainter painter) {
		EnsureRows();
		painter.PushClip(ScreenX, ScreenY, Wide, Tall);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float height = RowHeight * scale;
		float top = ScreenY + HeaderHeight - _scrollY;
		int first = Math.Max(0, (int)((_scrollY - HeaderHeight) / height));

		for (int i = first; i < _rows.Count; i++) {
			float rowTop = top + (i * height);

			if (rowTop >= ScreenY + Tall) break;

			if (!painter.NeedsPaint(ScreenX, rowTop, Wide, height)) continue;

			PaintRow(painter, i, rowTop, height, scale);
		}
	}

	public override void PostChildPaint(IPainter painter) => painter.PopClip();

	private void PaintRow(IPainter painter, int index, float top, float height, float scale) {
		ScmRow row = _rows[index];
		bool hovered = index == _hover;
		bool selected = row.Key == _selectedKey;
		float centreY = top + (height / 2f);
		float indent = ScreenX + (row.Depth * Indent * scale);
		float iconSize = Metrics.IconSize * scale;

		if (selected) painter.FillRectangle(ScreenX, top, Wide, height, Theme.ListActiveSelectionBackground);
		else if (hovered) painter.FillRectangle(ScreenX, top, Wide, height, Theme.ListHoverBackground);

		for (int level = 1; level < row.Depth; level++) painter.FillRectangle(ScreenX + (((level * Indent) + (Twistie / 2f)) * scale), top, Metrics.StrokeThickness * scale, height, Theme.TreeIndentGuide);

		float content = indent + ((Twistie + TwistiePadding) * scale);

		if (row.Kind != ScmRowKind.File) painter.DrawIcon(_view.IsCollapsed(row.Key) ? Codicon.ChevronRight : Codicon.ChevronDown, indent + (Twistie * scale / 2f), centreY, iconSize, Theme.Foreground);

		float right = Wide - (ContentPaddingRight * scale) + ScreenX;
		float actionsRight = ScreenX + ActionsRight(row, scale);
		bool showActions = hovered || selected;
		float labelRight = showActions ? actionsRight - (row.Actions.Length * ActionSize * scale) : actionsRight;

		switch (row.Kind) {
			case ScmRowKind.Group:
				PaintLabel(painter, painter.GetUi(), row.Label, null, content, top, height, labelRight, Theme.Foreground, false);
				Badge.Draw(painter, row.Count, right - BadgeWidth(row.Count, scale), centreY, scale, Theme.BadgeBackground, Theme.BadgeForeground);
				break;

			case ScmRowKind.Folder:
				if (!painter.DrawFolderIcon(row.Label, !_view.IsCollapsed(row.Key), content + (iconSize / 2f), centreY, iconSize)) painter.DrawIcon(Codicon.Folder, content + (iconSize / 2f), centreY, iconSize, Theme.Foreground);

				PaintLabel(painter, painter.GetUi(), row.Label, null, content + iconSize + (IconGap * scale), top, height, labelRight, Theme.Foreground, false);
				break;

			default:
				Color color = GitStatusColor.Of(row.Entry.Status) ?? Theme.SidebarFileForeground;

				painter.DrawFileIcon(row.Label, content + (iconSize / 2f), centreY, iconSize);

				string? description = _view.TreeView ? null : _view.TrimRoot(Path.GetDirectoryName(row.Entry.Path) ?? string.Empty);
				PaintLabel(painter, painter.GetUi(), row.Label, description, content + iconSize + (IconGap * scale), top, height, labelRight, color, row.Entry.Status == GitFileStatus.Deleted);

				string letter = GitStatusLetter.Of(row.Entry.Status);

				if (letter.Length > 0) {
					ITextFont font = painter.GetUi();
					float slot = DecorationWidth * scale;
					painter.DrawText(font, letter, right - slot + ((slot - font.Measure(letter)) / 2f), top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline(), color);
				}

				break;
		}

		if (!showActions) return;

		float actionRight = actionsRight;

		for (int i = row.Actions.Length - 1; i >= 0; i--) {
			float left = actionRight - (ActionSize * scale);
			bool hot = hovered && i == _hoverAction;

			if (hot) painter.FillRoundedRectangle(left, centreY - (ActionSize * scale / 2f), ActionSize * scale, ActionSize * scale, Metrics.RadiusSmall * scale, index == _pressed && i == _pressedAction ? Theme.PressedOverlay.Over(Theme.ToolbarHoverBackground) : Theme.ToolbarHoverBackground);

			painter.DrawIcon(row.Actions[i].Icon, left + (ActionSize * scale / 2f), centreY, Metrics.IconSize * scale, Theme.Foreground);
			actionRight = left;
		}
	}

	private static void PaintLabel(IPainter painter, ITextFont font, string label, string? description, float left, float top, float height, float right, Color color, bool strikethrough) {
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float width = Math.Max(0f, right - left);
		float nameWidth = font.Measure(label);

		painter.PushClip(left, top, width, height);
		painter.DrawText(font, label, left, baseline, color);

		if (strikethrough) painter.DrawHorizontalLine(MathF.Round(top + (height / 2f)), left, left + nameWidth, color);

		if (description is { Length: > 0 }) painter.DrawText(font, description, left + nameWidth + (Metrics.Space6 * Scale), baseline, Theme.DescriptionForeground);

		painter.PopClip();
	}

	#endregion
}
