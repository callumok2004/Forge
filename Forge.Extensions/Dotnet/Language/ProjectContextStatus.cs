using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Extensions.Dotnet.Language;

public sealed class ProjectContextStatus(RoslynLanguageClient server, SolutionWorkspace workspace, ProjectDefines defines, DiagnosticsCoordinator diagnostics, DotnetOutput output)
{
	private const string StatusId = "dotnet.projectContext";
	private const string SelectCommand = "dotnet.selectProjectContext";

	private readonly HashSet<string> _pinned = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();
	private Task? _updating;
	private bool _pending;

	public void Register(ICommandRegistry commands) => commands.Register(new Command(SelectCommand, "Select Project Context for Active File", ".NET", Pick));

	public void Forget(string path) {
		lock (_sync) _pinned.Remove(path);
	}

	public void Update(ICommandHost host) {
		lock (_sync) {
			if (_updating is not null) {
				_pending = true;
				return;
			}

			_updating = Task.Run(() => RunAsync(host));
		}
	}

	private async Task RunAsync(ICommandHost host) {
		while (true) {
			try {
				await RefreshAsync(host).ConfigureAwait(false);
			}
			catch (Exception exception) {
				output.Log(host, $"project context update failed: {exception.GetType().Name}: {exception.Message}");
			}

			lock (_sync) {
				if (!_pending) {
					_updating = null;
					return;
				}

				_pending = false;
			}
		}
	}

	private async Task RefreshAsync(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path || !ProjectDiscovery.IsSource(path)) {
			host.RemoveStatusItem(StatusId);
			host.Refresh();
			return;
		}

		ProjectContexts? list = server.Contexts.For(path);

		if (list is not null && list.All.All(static context => context.IsMiscellaneous)) list = null;

		if (list is null) {
			list = await server.Contexts.RequestAsync(path).ConfigureAwait(false);

			if (list is not null) output.Log(host, $"project contexts for {Path.GetFileName(path)}: {string.Join(", ", list.All.Select(static c => $"{c.Label} [{c.Id}]{(c.IsMiscellaneous ? " (misc)" : string.Empty)}"))}");
		}

		if (list is null) {
			if (workspace.Target is { } target) host.SetStatusItem(StatusId, "Project", Path.GetFileNameWithoutExtension(target), SelectCommand, rightAligned: true, priority: 100);
			else host.RemoveStatusItem(StatusId);

			host.Refresh();
			return;
		}

		string? current = list.Current?.Id;
		string? chosen = current;
		bool pinned;

		lock (_sync) pinned = _pinned.Contains(path);

		if (!pinned && list.All.Count > 1 && host.GetDocumentText(path) is { } text && await AutoSelectAsync(text, host.GetCaretLine() - 1, list, current).ConfigureAwait(false) is { } auto) chosen = auto;

		if (chosen is not null && chosen != server.Contexts.For(path)?.SelectedId) {
			server.Contexts.Select(path, chosen);

			if (chosen != current) {
				output.Log(host, $"{Path.GetFileName(path)} is now analysed as part of {LabelOf(list, chosen)}.");
				diagnostics.Refresh(path, immediate: true);
			}
		}

		Render(host, server.Contexts.For(path) ?? list);
	}

	private async Task<string?> AutoSelectAsync(string text, int line, ProjectContexts list, string? current) {
		if (!Preprocessor.IsConditionalAt(text, line)) return list.DefaultIndex < list.All.Count ? list.All[list.DefaultIndex].Id : null;

		async Task<bool> Satisfies(ProjectContext context) {
			if (context.IsMiscellaneous) return false;

			IReadOnlySet<string> defined = await defines.For(context).ConfigureAwait(false);
			return defined.Count > 0 && Preprocessor.IsActiveAt(text, line, defined);
		}

		foreach (ProjectContext context in list.All) {
			if (context.Id == current && await Satisfies(context).ConfigureAwait(false)) return null;
		}

		foreach (ProjectContext context in list.All) {
			if (await Satisfies(context).ConfigureAwait(false)) return context.Id;
		}

		return null;
	}

	private static string LabelOf(ProjectContexts list, string? id) {
		foreach (ProjectContext context in list.All) {
			if (context.Id == id) return context.Label;
		}

		return "unknown";
	}

	private static void Render(ICommandHost host, ProjectContexts list) {
		if (list.All.Count == 0) {
			host.RemoveStatusItem(StatusId);
			host.Refresh();
			return;
		}

		host.SetStatusItem(StatusId, "Project", LabelOf(list, list.Current?.Id), SelectCommand, rightAligned: true, priority: 100);
		host.Refresh();
	}

	private void Pick(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path || !ProjectDiscovery.IsSource(path) || server.Contexts.For(path) is not { } list) return;

		List<QuickPickItem> items = [with(list.All.Count + 1)];

		foreach (ProjectContext context in list.All) items.Add(new QuickPickItem(context.Label, context.Id == list.Current?.Id ? "current" : string.Empty));

		items.Add(new QuickPickItem("Automatic", "Follow the conditional block at the caret"));

		host.ShowQuickPick("Project used to analyse this file", items, index => {
			if ((uint)index > (uint)list.All.Count) return;

			if (index < list.All.Count) {
				lock (_sync) _pinned.Add(path);

				server.Contexts.Select(path, list.All[index].Id);
				diagnostics.Refresh(path, immediate: true);
			}
			else {
				lock (_sync) _pinned.Remove(path);

				server.Contexts.Select(path, null);
			}

			Update(host);
		});
	}
}
