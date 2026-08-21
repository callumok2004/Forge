using Forge.Common.Render;
using Forge.Core;
using Forge.Theming;

namespace Forge.Git;

public enum GitCommand
{
	None,

	Commit,
	CommitStaged,
	CommitAll,
	UndoCommit,
	CommitAmend,
	CommitStagedAmend,
	CommitAllAmend,
	CommitNoVerify,

	StageAll,
	UnstageAll,
	DiscardAll,

	Sync,
	Pull,
	PullRebase,
	Push,
	PushForce,
	Fetch,
	FetchPrune,
	FetchAll,

	Checkout,
	Merge,
	Rebase,
	CreateBranch,
	RenameBranch,
	DeleteBranch,
	Publish,

	AddRemote,
	RemoveRemote,

	Stash,
	StashIncludeUntracked,
	StashStaged,
	StashApplyLatest,
	StashApply,
	StashPopLatest,
	StashPop,
	StashDrop,

	CreateTag,
	DeleteTag,
	PushTags,

	Refresh,
	ShowOutput,

	ViewAsTree,
	ViewAsList,
}

public readonly record struct GitMenuItem(string? Label, GitCommand Command, GitMenuItem[]? Submenu = null)
{
	public bool IsSeparator => Label is null;

	public static GitMenuItem Separator => new(null, GitCommand.None);
}

public sealed class GitMenu
{
	private const float RowHeight = 22f;
	private const float SeparatorHeight = 7f;
	private const float PaddingY = 4f;
	private const float PaddingX = 10f;

	private const float SubmenuGap = 28f;

	private static readonly GitMenuItem[] Items =
	[
		new("Pull", GitCommand.Pull),
		new("Push", GitCommand.Push),
		new("Clone", GitCommand.None),
		new("Checkout to...", GitCommand.Checkout),
		new("Fetch", GitCommand.Fetch),
		GitMenuItem.Separator,

		new("Commit", GitCommand.None,
		[
			new("Commit", GitCommand.Commit),
			new("Commit Staged", GitCommand.CommitStaged),
			new("Commit All", GitCommand.CommitAll),
			new("Undo Last Commit", GitCommand.UndoCommit),
			GitMenuItem.Separator,
			new("Commit (No Verify)", GitCommand.CommitNoVerify),
			GitMenuItem.Separator,
			new("Commit (Amend)", GitCommand.CommitAmend),
			new("Commit Staged (Amend)", GitCommand.CommitStagedAmend),
			new("Commit All (Amend)", GitCommand.CommitAllAmend),
		]),
		new("Changes", GitCommand.None,
		[
			new("Stage All Changes", GitCommand.StageAll),
			new("Unstage All Changes", GitCommand.UnstageAll),
			new("Discard All Changes", GitCommand.DiscardAll),
		]),
		new("Pull, Push", GitCommand.None,
		[
			new("Sync", GitCommand.Sync),
			GitMenuItem.Separator,
			new("Pull", GitCommand.Pull),
			new("Pull (Rebase)", GitCommand.PullRebase),
			GitMenuItem.Separator,
			new("Push", GitCommand.Push),
			new("Push (Force)", GitCommand.PushForce),
			GitMenuItem.Separator,
			new("Fetch", GitCommand.Fetch),
			new("Fetch (Prune)", GitCommand.FetchPrune),
			new("Fetch From All Remotes", GitCommand.FetchAll),
		]),
		new("Branch", GitCommand.None,
		[
			new("Merge...", GitCommand.Merge),
			new("Rebase Branch...", GitCommand.Rebase),
			GitMenuItem.Separator,
			new("Create Branch...", GitCommand.CreateBranch),
			GitMenuItem.Separator,
			new("Rename Branch...", GitCommand.RenameBranch),
			new("Delete Branch...", GitCommand.DeleteBranch),
			GitMenuItem.Separator,
			new("Publish Branch...", GitCommand.Publish),
		]),
		new("Remote", GitCommand.None,
		[
			new("Add Remote...", GitCommand.AddRemote),
			new("Remove Remote", GitCommand.RemoveRemote),
		]),
		new("Stash", GitCommand.None,
		[
			new("Stash", GitCommand.Stash),
			new("Stash (Include Untracked)", GitCommand.StashIncludeUntracked),
			new("Stash Staged", GitCommand.StashStaged),
			GitMenuItem.Separator,
			new("Apply Latest Stash", GitCommand.StashApplyLatest),
			new("Apply Stash...", GitCommand.StashApply),
			GitMenuItem.Separator,
			new("Pop Latest Stash", GitCommand.StashPopLatest),
			new("Pop Stash...", GitCommand.StashPop),
			GitMenuItem.Separator,
			new("Drop Stash...", GitCommand.StashDrop),
		]),
		new("Tags", GitCommand.None,
		[
			new("Create Tag...", GitCommand.CreateTag),
			new("Delete Tag...", GitCommand.DeleteTag),
			GitMenuItem.Separator,
			new("Push Tags", GitCommand.PushTags),
		]),
		new("Worktrees", GitCommand.None,
		[
			new("Nothing here yet", GitCommand.None),
		]),
		GitMenuItem.Separator,

		new("View as Tree", GitCommand.ViewAsTree),
		new("View as List", GitCommand.ViewAsList),
		GitMenuItem.Separator,

		new("Show Git Output", GitCommand.ShowOutput),
	];

	private bool _open;
	private int _selected = -1;

	private int _expanded = -1;

	private int _submenuSelected = -1;

	private float _left;
	private float _top;
	private float _width;
	private float _height;

	private float _subLeft;
	private float _subTop;
	private float _subWidth;
	private float _subHeight;

	private float _anchorRight;
	private float _anchorTop;

	private float _scale = 1f;

	public bool IsOpen => _open;

	public void Open(float right, float top) {
		_open = true;
		_anchorRight = right;
		_anchorTop = top;
		_selected = -1;
		_expanded = -1;
		_submenuSelected = -1;
	}

	public void Close() {
		_open = false;
		_selected = -1;
		_expanded = -1;
		_submenuSelected = -1;
	}

	public PixelRect Bounds {
		get {
			if (!_open) return PixelRect.Empty;

			PixelRect bounds = PixelRect.Around(_left - 2f, _top - 2f, _width + 4f, _height + 4f);

			return _expanded >= 0
				? bounds.Union(PixelRect.Around(_subLeft - 2f, _subTop - 2f, _subWidth + 4f, _subHeight + 4f))
				: bounds;
		}
	}

	public bool Contains(float x, float y) =>
		_open && ((x >= _left && x < _left + _width && y >= _top && y < _top + _height) ||
			(_expanded >= 0 && x >= _subLeft && x < _subLeft + _subWidth &&
				y >= _subTop && y < _subTop + _subHeight));

	public bool Hover(float x, float y) {
		if (!_open) return false;

		int sub = SubmenuRowAt(x, y);

		if (sub >= 0) {
			_submenuSelected = sub;
			return true;
		}

		int row = RowAt(x, y);
		_selected = row;
		_submenuSelected = -1;

		if (row >= 0) _expanded = Items[row].Submenu is null ? -1 : row;

		return Contains(x, y);
	}

	public GitCommand CommandAt(float x, float y) {
		int sub = SubmenuRowAt(x, y);

		if (sub >= 0) return Items[_expanded].Submenu![sub].Command;

		int row = RowAt(x, y);

		return row >= 0 && Items[row].Submenu is null ? Items[row].Command : GitCommand.None;
	}

	private int RowAt(float x, float y) {
		if (!_open || x < _left || x >= _left + _width || y < _top || y >= _top + _height) return -1;

		return Walk(Items, _top, y);
	}

	private int SubmenuRowAt(float x, float y) {
		if (!_open || _expanded < 0 || x < _subLeft || x >= _subLeft + _subWidth ||
			y < _subTop || y >= _subTop + _subHeight) return -1;

		return Walk(Items[_expanded].Submenu!, _subTop, y);
	}

	private int Walk(GitMenuItem[] items, float top, float y) {
		float cursor = top + (PaddingY * _scale);

		for (int i = 0; i < items.Length; i++) {
			float height = (items[i].IsSeparator ? SeparatorHeight : RowHeight) * _scale;

			if (!items[i].IsSeparator && y >= cursor && y < cursor + height) return i;

			cursor += height;
		}

		return -1;
	}

	private static (float Width, float Height) Measure(GitMenuItem[] items, ITextFont font, float scale) {
		float widest = 0f;
		float content = 0f;

		foreach (GitMenuItem item in items) {
			content += (item.IsSeparator ? SeparatorHeight : RowHeight) * scale;

			if (!item.IsSeparator) widest = Math.Max(widest, font.Measure(item.Label!));
		}

		return (widest + (PaddingX * 2f) + (SubmenuGap * scale), content + (PaddingY * 2f * scale));
	}

	public void Render(IPainter painter, float windowWidth, float windowHeight, float scale) {
		if (!_open) return;

		_scale = scale;

		ITextFont font = painter.GetUi();

		(_width, _height) = Measure(Items, font, scale);

		_left = Math.Max(0f, Math.Min(_anchorRight - _width, windowWidth - _width));
		_top = Math.Min(_anchorTop, Math.Max(0f, windowHeight - _height));

		DrawPanel(painter, Items, _left, _top, _width, _height, _selected, scale);

		if (_expanded < 0 || Items[_expanded].Submenu is not { } submenu) return;

		(_subWidth, _subHeight) = Measure(submenu, font, scale);

		_subLeft = Math.Max(0f, Math.Min(_left + _width, windowWidth - _subWidth));
		_subTop = Math.Min(RowTop(_expanded), Math.Max(0f, windowHeight - _subHeight));

		DrawPanel(painter, submenu, _subLeft, _subTop, _subWidth, _subHeight, _submenuSelected, scale);
	}

	private float RowTop(int index) {
		float cursor = _top + (PaddingY * _scale);

		for (int i = 0; i < index; i++) cursor += (Items[i].IsSeparator ? SeparatorHeight : RowHeight) * _scale;

		return cursor - (PaddingY * _scale);
	}

	private static void DrawPanel(IPainter painter, GitMenuItem[] items, float left, float top, float width,
		float height, int selected, float scale) {
		ITextFont font = painter.GetUi();
		float padX = PaddingX * scale;
		float padY = PaddingY * scale;
		float radius = Metrics.RadiusLarge * scale;

		painter.PushRoundedClip(left, top, width, height, radius);
		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.WidgetBackground);

		float cursor = top + padY;

		for (int i = 0; i < items.Length; i++) {
			GitMenuItem item = items[i];

			if (item.IsSeparator) {
				float ruleY = cursor + ((SeparatorHeight * scale) / 2f);
				painter.DrawHorizontalLine(ruleY, left + padX, left + width - padX, Theme.WidgetBorder);
				cursor += SeparatorHeight * scale;
				continue;
			}

			float rowHeight = RowHeight * scale;

			if (i == selected) {
				float inset = Metrics.Space4 * scale;
				painter.FillRoundedRectangle(left + inset, cursor, width - (inset * 2f), rowHeight,
					Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);
			}

			float baseline = cursor + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

			Color color = item.Command == GitCommand.None && item.Submenu is null
				? Theme.SectionHeaderForeground
				: i == selected ? Theme.TabActiveForeground : Theme.EditorForeground;

			painter.DrawText(font, item.Label!, left + padX, baseline, color);

			if (item.Submenu is not null) {
				float chevron = Metrics.IconSize * scale;

				painter.DrawIcon(Codicon.ChevronRight, left + width - padX - (chevron / 2f),
					cursor + (rowHeight / 2f), chevron, color);
			}

			cursor += rowHeight;
		}

		painter.PopRoundedClip();

		painter.StrokeRoundedRectangle(left, top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}
}
