using Forge.Common.Git;
using Forge.Core.Native;
using Forge.Git;

namespace Forge.Shell;

public static class SourceControlCommands
{
	public static void HandleSourceControlClick(Workbench workbench, int x, int y, Action refresh) {
		SourceControlView view = workbench.Sidebar.SourceControl;

		SourceControlHit hit = view.HitTest(x, y, workbench.Scale, out GitEntry entry, out bool staged);

		if (hit != view.Pressed) return;

		if (workbench.Repository is not { } repository) {
			refresh();
			return;
		}

		switch (hit) {
			case SourceControlHit.Twistie when view.TwistieKey is { } key:
				view.Toggle(key);
				break;

			case SourceControlHit.File:
				workbench.OpenFile(entry.Path);
				break;

			case SourceControlHit.Stage:
				RunGit(workbench, refresh, () => repository.Stage(entry.Path));
				break;

			case SourceControlHit.Unstage:
				RunGit(workbench, refresh, () => repository.Unstage(entry.Path));
				break;

			case SourceControlHit.Discard:
				if (Win32.Confirm($"Discard changes to {Path.GetFileName(entry.Path)}?",
					"This cannot be undone.")) {
					RunGit(workbench, refresh,
						() => repository.Discard(entry.Path, entry.Status == GitFileStatus.Untracked));
				}

				break;

			case SourceControlHit.Commit:
			case SourceControlHit.TitleCommit:
				HandleCommit(workbench, refresh);
				break;

			case SourceControlHit.TitleRefresh:
				RunGit(workbench, refresh, static () => true);
				break;

			case SourceControlHit.TitleMore:
				view.OpenMenu();
				break;

			case SourceControlHit.Sync:
				InvokeGit(workbench, GitCommand.Sync, refresh);
				break;

			case SourceControlHit.StageAll:
				InvokeGit(workbench, GitCommand.StageAll, refresh);
				break;

			case SourceControlHit.UnstageAll:
				InvokeGit(workbench, GitCommand.UnstageAll, refresh);
				break;

			case SourceControlHit.DiscardAll:
				InvokeGit(workbench, GitCommand.DiscardAll, refresh);
				break;
		}

		refresh();
	}

	public static void InvokeGit(Workbench workbench, GitCommand command, Action refresh) {
		SourceControlView view = workbench.Sidebar.SourceControl;

		switch (command) {
			case GitCommand.ViewAsTree:
				view.TreeView = true;
				refresh();
				return;

			case GitCommand.ViewAsList:
				view.TreeView = false;
				refresh();
				return;

			case GitCommand.Commit:
			case GitCommand.CommitStaged:
				HandleCommit(workbench, refresh);
				return;
		}

		if (workbench.Repository is not { } repository) return;

		string root = repository.GetRootPath();
		string message = view.Message;

		switch (command) {
			case GitCommand.StageAll:
				RunGit(workbench, refresh, () => repository.Stage("."));
				return;

			case GitCommand.UnstageAll:
				RunGit(workbench, refresh,
					() => GitProcess.Run(root, "reset", "--quiet", "HEAD", "--", ".") is not null);
				return;

			case GitCommand.DiscardAll:
				if (Win32.Confirm("Discard all changes?",
					"Every change in the working tree will be thrown away. This cannot be undone.")) {
					RunGit(workbench, refresh,
						() => GitProcess.Run(root, "checkout", "--", ".") is not null);
				}

				return;

			case GitCommand.CommitAll:
				RunGit(workbench, refresh, () => {
					if (message.Trim().Length > 0 &&
						GitProcess.Run(root, "commit", "-a", "-m", message) is not null) view.ClearMessage();

					return true;
				});

				return;
		}

		string[]? arguments = command switch {
			GitCommand.UndoCommit => ["reset", "--soft", "HEAD~1"],
			GitCommand.CommitAmend => ["commit", "--amend", "--no-edit"],
			GitCommand.CommitStagedAmend => ["commit", "--amend", "--no-edit"],
			GitCommand.CommitAllAmend => ["commit", "-a", "--amend", "--no-edit"],
			GitCommand.CommitNoVerify => ["commit", "--no-verify", "-m", message],
			GitCommand.Sync => ["pull", "--tags", "--rebase"],
			GitCommand.Pull => ["pull"],
			GitCommand.PullRebase => ["pull", "--rebase"],
			GitCommand.Push => ["push"],
			GitCommand.PushForce => ["push", "--force-with-lease"],
			GitCommand.Fetch => ["fetch"],
			GitCommand.FetchPrune => ["fetch", "--prune"],
			GitCommand.FetchAll => ["fetch", "--all"],
			GitCommand.Publish => ["push", "--set-upstream", "origin", repository.GetBranch() ?? "HEAD"],
			GitCommand.Stash => ["stash", "push"],
			GitCommand.StashIncludeUntracked => ["stash", "push", "--include-untracked"],
			GitCommand.StashStaged => ["stash", "push", "--staged"],
			GitCommand.StashApplyLatest => ["stash", "apply"],
			GitCommand.StashPopLatest => ["stash", "pop"],
			GitCommand.PushTags => ["push", "--tags"],
			_ => null,
		};

		if (arguments is not null) RunGit(workbench, refresh, () => GitProcess.Run(root, arguments) is not null);
	}

	public static void HandleCommit(Workbench workbench, Action refresh) {
		if (workbench.Repository is not { } repository) return;

		SourceControlView view = workbench.Sidebar.SourceControl;
		string message = view.Message;

		RunGit(workbench, refresh, () => {
			if (repository.Commit(message)) view.ClearMessage();

			return true;
		});
	}

	public static void RunGit(Workbench workbench, Action refresh, Func<bool> command) {
		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			command();
			workbench.Repository?.Refresh();
			refresh();
		}, null);
	}
}
