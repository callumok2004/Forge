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

		foreach (GitInvocation invocation in Invocations) Add(invocation.Id, invocation.Title, () => Run(workbench, context, (_, root) => GitProcess.Run(root, invocation.Arguments) is not null));

		Add("git.refresh", "Refresh", () => Run(workbench, context, static (_, _) => true));
		Add("git.commit", "Commit", () => Commit(workbench, context, all: false));
		Add("git.commitStaged", "Commit Staged", () => Commit(workbench, context, all: false));
		Add("git.commitAll", "Commit All", () => Commit(workbench, context, all: true));
		Add("git.commitNoVerify", "Commit (No Verify)", () => WithRepository(workbench, context, repository => {
			string message = view.MessageFor(repository);

			Run(context, repository, (_, root) => GitProcess.Run(root, "commit", "--no-verify", "-m", message) is not null && Cleared(workbench, repository, message));
		}));
		Add("git.commitAndPush", "Commit & Push", () => Commit(workbench, context, all: false, then: ["push"]));
		Add("git.commitAndSync", "Commit & Sync", () => Commit(workbench, context, all: false, then: ["pull", "--tags", "--rebase"]));
		Add("git.publish", "Publish Branch", () => Run(workbench, context, static (repository, root) => GitProcess.Run(root, "push", "--set-upstream", "origin", repository.GetBranch() ?? "HEAD") is not null));
		Add("git.close", "Close Repository", () => WithRepository(workbench, context, repository => {
			workbench.Repositories?.Close(repository);
			context.Refresh();
		}));
		Add("git.showOutput", "Show Git Output", () => {
			workbench.Panel.Show(PanelView.Output);
			IOutputChannels channels = ForgeDllMain.Channels;
			workbench.Panel.ActivateChannel(channels.All().ToList().IndexOf(channels.Get("Git")));
		});

		Add("git.cleanAll", "Discard All Changes", () => {
			WithRepository(workbench, context, repository => Forge.UI.Controls.ConfirmDialog.Show("Are you sure you want to discard ALL changes?", "This action is irreversible!", "Discard All Changes", () => {
				Run(context, repository, static (_, root) => GitProcess.Run(root, "checkout", "--", ".") is not null);
				context.Refresh();
			}));
		});

		Add("scm.acceptInput", "Commit", () => {
			if (view.FocusedInputRepository() is { } focused) view.RunCommand("git.commit", focused);
			else context.Run("git.commit");
		});

		registry.Register(new Command("workbench.scm.action.setTreeViewMode", "View as Tree", "Source Control", _ => view.TreeView = true));
		registry.Register(new Command("workbench.scm.action.setListViewMode", "View as List", "Source Control", _ => view.TreeView = false));

		view.RunGit = (repository, command) => Run(context, repository, (target, _) => command(target));
		view.CommandRequested = (command, repository) => {
			workbench.CommandRepository = repository;

			try {
				context.Run(command);
			}
			finally {
				workbench.CommandRepository = null;
			}
		};
		view.RepositoryFocused = workbench.FocusRepository;
		view.FileOpened = path => {
			workbench.OpenFile(path);
			context.Refresh();
		};
		ScmGraphView graph = workbench.Sidebar.Graph;
		graph.Changed = context.Refresh;
		graph.ChangesOpened = (title, from, to) => {
			if (graph.Repository is not { } repository) return;

			string root = repository.GetRootPath();

			ThreadPool.UnsafeQueueUserWorkItem(_ => {
				IReadOnlyList<GitCommitChange> changes = GitHistoryReader.Changes(root, from, to);

				workbench.Post(() => {
					workbench.Views?.OpenEditor("commitDiff:" + from + ".." + to, () => new CommitDiffPane(root, title, from, to, changes, path => {
						workbench.OpenFile(path);
						context.Refresh();
					}, context.Refresh));
					context.Refresh();
				});
			}, null);
		};
		graph.ChangeOpened = (commit, change) => {
			if (graph.Repository is not { } repository) return;

			workbench.OpenCommitChange(repository.GetRootPath(), commit, change);
			context.Refresh();
		};
		registry.Register(new Command("workbench.scm.history.refresh", "Refresh", "Source Control", _ => {
			graph.Refresh();
			context.Refresh();
		}));

		view.ChangeOpened = (entry, staged) => {
			workbench.OpenGitChange(entry.Path, entry.Status, staged);
			context.Refresh();
		};
	}

	private static bool Cleared(Workbench workbench, IGitRepository repository, string message) {
		workbench.Post(() => {
			if (workbench.Sidebar.SourceControl.MessageFor(repository) == message) workbench.Sidebar.SourceControl.ClearMessage(repository);
		});

		return true;
	}

	private static void Commit(Workbench workbench, ShellContext context, bool all, string[]? then = null) => WithRepository(workbench, context, repository => {
		SourceControlView view = workbench.Sidebar.SourceControl;
		string message = view.MessageFor(repository);

		if (message.Trim().Length == 0) {
			view.InputFor(repository)?.RequestFocus();
			return;
		}

		if (view.IsCommitting(repository)) return;

		bool smart = all || (repository.GetStaged().Count == 0 && context.Settings.GetBoolean("git.enableSmartCommit") == true);
		string root = repository.GetRootPath();

		view.SetCommitting(repository, true);

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			bool committed = smart ? GitProcess.RunUntimed(root, "commit", "-a", "-m", message) is not null : repository.Commit(message);

			workbench.Post(() => {
				view.SetCommitting(repository, false);

				if (committed && view.MessageFor(repository) == message) view.ClearMessage(repository);

				context.Refresh();
			});

			if (committed && then is not null) GitProcess.Run(root, then);

			repository.Refresh();
			context.Refresh();
		}, null);
	});

	private static void Run(Workbench workbench, ShellContext context, Func<IGitRepository, string, bool> command) => WithRepository(workbench, context, repository => Run(context, repository, command));

	private static void Run(ShellContext context, IGitRepository repository, Func<IGitRepository, string, bool> command) {
		string root = repository.GetRootPath();

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			command(repository, root);
			repository.Refresh();
			context.Refresh();
		}, null);
	}

	public static void WithRepository(Workbench workbench, ShellContext context, Action<IGitRepository> action) {
		if (workbench.CommandRepository is { } chosen) {
			workbench.CommandRepository = null;
			action(chosen);
			return;
		}

		IReadOnlyList<IGitRepository> all = workbench.Repositories?.GetAll() ?? [];

		if (all.Count == 0) return;

		if (all.Count == 1) {
			action(all[0]);
			return;
		}

		List<IGitRepository> picks = [.. all];

		if (workbench.RepositoryFor(workbench.ActiveDocument?.SourcePath) is { } active && picks.Remove(active)) picks.Insert(0, active);

		QuickPickItem[] items = [.. picks.Select(static repository => new QuickPickItem(System.IO.Path.GetFileName(repository.GetRootPath()), Description(repository)))];

		workbench.QuickInput.Show(new Forge.Commands.ListQuickPick("Choose a repository", items, index => {
			if (index >= 0 && index < picks.Count) action(picks[index]);

			context.Refresh();
		}));
	}

	private static string Description(IGitRepository repository) {
		string head = repository.GetBranch() ?? "HEAD";

		if (repository.GetUnstaged().Count > 0) head += "*";

		if (repository.GetStaged().Count > 0) head += "+";

		if (repository.GetMerge().Count > 0) head += "!";

		int behind = repository.GetBehind();
		int ahead = repository.GetAhead();

		return behind > 0 || ahead > 0 ? $"{head} {behind}↓ {ahead}↑" : head;
	}
}
