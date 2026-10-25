using Forge.Common.Commands;
using Forge.Common.Git;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Git;

namespace Forge.Shell.Actions;

public static class GitCommands
{
	private readonly record struct GitInvocation(string Id, string Title, string[] Arguments);

	private static readonly GitInvocation[] Invocations =
	[
		new("git.undoCommit", "Undo Last Commit", ["reset", "--soft", "HEAD~1"]),
		new("git.commitAmend", "Commit (Amend)", ["commit", "--amend", "--no-edit"]),
		new("git.commitStagedAmend", "Commit Staged (Amend)", ["commit", "--amend", "--no-edit"]),
		new("git.commitAllAmend", "Commit All (Amend)", ["commit", "-a", "--amend", "--no-edit"]),
		new("git.sync", "Sync", ["pull", "--tags", "--rebase"]),
		new("git.pull", "Pull", ["pull"]),
		new("git.pullRebase", "Pull (Rebase)", ["pull", "--rebase"]),
		new("git.push", "Push", ["push"]),
		new("git.pushForce", "Push (Force)", ["push", "--force-with-lease"]),
		new("git.fetch", "Fetch", ["fetch"]),
		new("git.fetchPrune", "Fetch (Prune)", ["fetch", "--prune"]),
		new("git.fetchAll", "Fetch From All Remotes", ["fetch", "--all"]),
		new("git.stash", "Stash", ["stash", "push"]),
		new("git.stashIncludeUntracked", "Stash (Include Untracked)", ["stash", "push", "--include-untracked"]),
		new("git.stashStaged", "Stash Staged", ["stash", "push", "--staged"]),
		new("git.stashApplyLatest", "Apply Latest Stash", ["stash", "apply"]),
		new("git.stashPopLatest", "Pop Latest Stash", ["stash", "pop"]),
		new("git.pushTags", "Push Tags", ["push", "--tags"]),
		new("git.unstageAll", "Unstage All Changes", ["reset", "--quiet", "HEAD", "--", "."]),
		new("git.stageAll", "Stage All Changes", ["add", "-A", "--", "."]),
		new("git.stageAllMerge", "Stage All Merge Changes", ["add", "-u", "--", "."]),
	];

	public static void Register(ICommandRegistry registry, ShellContext context) {
		Workbench workbench = context.Workbench;
		SourceControlView view = workbench.Sidebar.SourceControl;
		bool HasRepository() => workbench.Repository is not null;

		void Add(string id, string title, Action action, string? keybinding = null, Func<bool>? when = null) => registry.Register(new Command(id, title, "Git", _ => action(), keybinding, when ?? HasRepository));

		foreach (GitInvocation invocation in Invocations) Add(invocation.Id, invocation.Title, () => Run(workbench, context, root => GitProcess.Run(root, invocation.Arguments) is not null));

		Add("git.refresh", "Refresh", () => Run(workbench, context, static _ => true));
		Add("git.commit", "Commit", () => Commit(workbench, context, all: false));
		Add("git.commitStaged", "Commit Staged", () => Commit(workbench, context, all: false));
		Add("git.commitAll", "Commit All", () => Commit(workbench, context, all: true));
		Add("git.commitNoVerify", "Commit (No Verify)", () => Run(workbench, context, root => GitProcess.Run(root, "commit", "--no-verify", "-m", view.Message) is not null && Cleared(view)));
		Add("git.commitAndPush", "Commit & Push", () => Commit(workbench, context, all: false, then: ["push"]));
		Add("git.commitAndSync", "Commit & Sync", () => Commit(workbench, context, all: false, then: ["pull", "--tags", "--rebase"]));
		Add("git.publish", "Publish Branch", () => Run(workbench, context, root => GitProcess.Run(root, "push", "--set-upstream", "origin", workbench.Repository?.GetBranch() ?? "HEAD") is not null));
		Add("git.showOutput", "Show Git Output", () => context.Run("workbench.action.output.toggleOutput"));

		Add("git.cleanAll", "Discard All Changes", () => {
			if (Win32.Confirm("Are you sure you want to discard ALL changes?", "This action is irreversible!")) Run(workbench, context, static root => GitProcess.Run(root, "checkout", "--", ".") is not null);
		});

		Add("scm.acceptInput", "Commit", () => context.Run("git.commit"), "Ctrl+Enter", () => Panel.Ui?.HasContext(ContextKeys.ScmInputFocused) ?? false);

		registry.Register(new Command("workbench.scm.action.setTreeViewMode", "View as Tree", "Source Control", _ => view.TreeView = true));
		registry.Register(new Command("workbench.scm.action.setListViewMode", "View as List", "Source Control", _ => view.TreeView = false));

		view.RunGit = command => Run(workbench, context, _ => workbench.Repository is { } repository && command(repository));
		view.FileOpened = path => {
			workbench.OpenFile(path);
			context.Refresh();
		};
	}

	private static bool Cleared(SourceControlView view) {
		view.ClearMessage();
		return true;
	}

	private static void Commit(Workbench workbench, ShellContext context, bool all, string[]? then = null) {
		if (workbench.Repository is not { } repository) return;

		SourceControlView view = workbench.Sidebar.SourceControl;
		string message = view.Message;

		if (message.Trim().Length == 0) {
			view.Input.RequestFocus();
			return;
		}

		bool smart = all || (repository.GetStaged().Count == 0 && context.Settings.GetBoolean("git.enableSmartCommit") == true);

		view.ClearMessage();

		Run(workbench, context, root => {
			bool committed = smart ? GitProcess.Run(root, "commit", "-a", "-m", message) is not null : repository.Commit(message);

			if (committed && then is not null) GitProcess.Run(root, then);

			return committed;
		});
	}

	private static void Run(Workbench workbench, ShellContext context, Func<string, bool> command) {
		if (workbench.Repository is not { } repository) return;

		string root = repository.GetRootPath();

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			command(root);
			repository.Refresh();
			context.Refresh();
		}, null);
	}
}
