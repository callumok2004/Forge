namespace Forge.Workbench;

public static class GitMenus
{
	public static readonly MenuEntry[] More =
	[
		new("Pull", "git.pull"),
		new("Push", "git.push"),
		new("Clone", "git.clone"),
		new("Checkout to...", "git.checkout"),
		new("Fetch", "git.fetch"),
		MenuEntry.Separator,
		MenuEntry.Submenu("Commit", [
			new("Commit", "git.commit"),
			new("Commit Staged", "git.commitStaged"),
			new("Commit All", "git.commitAll"),
			new("Undo Last Commit", "git.undoCommit"),
			MenuEntry.Separator,
			new("Commit (No Verify)", "git.commitNoVerify"),
			MenuEntry.Separator,
			new("Commit (Amend)", "git.commitAmend"),
			new("Commit Staged (Amend)", "git.commitStagedAmend"),
			new("Commit All (Amend)", "git.commitAllAmend"),
		]),
		MenuEntry.Submenu("Changes", [
			new("Stage All Changes", "git.stageAll"),
			new("Unstage All Changes", "git.unstageAll"),
			new("Discard All Changes", "git.cleanAll"),
		]),
		MenuEntry.Submenu("Pull, Push", [
			new("Sync", "git.sync"),
			MenuEntry.Separator,
			new("Pull", "git.pull"),
			new("Pull (Rebase)", "git.pullRebase"),
			MenuEntry.Separator,
			new("Push", "git.push"),
			new("Push (Force)", "git.pushForce"),
			MenuEntry.Separator,
			new("Fetch", "git.fetch"),
			new("Fetch (Prune)", "git.fetchPrune"),
			new("Fetch From All Remotes", "git.fetchAll"),
		]),
		MenuEntry.Submenu("Branch", [
			new("Merge...", "git.merge"),
			new("Rebase Branch...", "git.rebase"),
			MenuEntry.Separator,
			new("Create Branch...", "git.branch"),
			MenuEntry.Separator,
			new("Rename Branch...", "git.renameBranch"),
			new("Delete Branch...", "git.deleteBranch"),
			MenuEntry.Separator,
			new("Publish Branch...", "git.publish"),
		]),
		MenuEntry.Submenu("Remote", [
			new("Add Remote...", "git.addRemote"),
			new("Remove Remote", "git.removeRemote"),
		]),
		MenuEntry.Submenu("Stash", [
			new("Stash", "git.stash"),
			new("Stash (Include Untracked)", "git.stashIncludeUntracked"),
			new("Stash Staged", "git.stashStaged"),
			MenuEntry.Separator,
			new("Apply Latest Stash", "git.stashApplyLatest"),
			new("Apply Stash...", "git.stashApply"),
			MenuEntry.Separator,
			new("Pop Latest Stash", "git.stashPopLatest"),
			new("Pop Stash...", "git.stashPop"),
			MenuEntry.Separator,
			new("Drop Stash...", "git.stashDrop"),
		]),
		MenuEntry.Submenu("Tags", [
			new("Create Tag...", "git.createTag"),
			new("Delete Tag...", "git.deleteTag"),
			MenuEntry.Separator,
			new("Push Tags", "git.pushTags"),
		]),
		MenuEntry.Submenu("Worktrees", [
			new("Create Worktree...", "git.createWorktree"),
			new("Delete Worktree...", "git.deleteWorktree"),
		]),
		MenuEntry.Separator,
		new("Show Git Output", "git.showOutput"),
	];

	public static readonly MenuEntry[] Commit =
	[
		new("Commit", "git.commit"),
		new("Commit (Amend)", "git.commitAmend"),
		new("Commit & Push", "git.commitAndPush"),
		new("Commit & Sync", "git.commitAndSync"),
	];

	public static readonly MenuEntry[] ViewAndSort =
	[
		new("View as Tree", "workbench.scm.action.setTreeViewMode"),
		new("View as List", "workbench.scm.action.setListViewMode"),
	];
}
