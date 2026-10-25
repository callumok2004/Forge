using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Extensions.Dotnet.Language;

public sealed class ProjectContextStatus(RoslynLanguageClient server, SolutionWorkspace workspace, DotnetOutput output)
{
	private const string StatusId = "dotnet.projectContext";
	private const string SelectCommand = "dotnet.selectProjectContext";

	public void Register(ICommandRegistry commands) => commands.Register(new Command(SelectCommand, "Select Project Context for Active File", ".NET", SelectNext));

	public void Update(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path || !ProjectDiscovery.IsSource(path)) {
			host.RemoveStatusItem(StatusId);
			return;
		}

		_ = Task.Run(async () => {
			if (await server.Contexts.RequestAsync(path).ConfigureAwait(false) is { Current: { } current } contexts) {
				string label = contexts.All.Count > 1 ? $"{current.Label} (+{contexts.All.Count - 1})" : current.Label;
				host.SetStatusItem(StatusId, "Project", label, SelectCommand, rightAligned: true, priority: 100);
			}
			else if (workspace.Target is { } target) host.SetStatusItem(StatusId, "Project", Path.GetFileNameWithoutExtension(target), SelectCommand, rightAligned: true, priority: 100);
			else host.RemoveStatusItem(StatusId);

			host.Refresh();
		});
	}

	private void SelectNext(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path || !ProjectDiscovery.IsSource(path)) return;

		ProjectContexts? contexts = server.Contexts.For(path);

		if (contexts is not { All.Count: > 1 }) {
			output.Log(host, contexts?.Current is { } only ? $"{Path.GetFileName(path)} is analysed as part of {only.Label}." : $"No project context for {Path.GetFileName(path)}.");
			host.Refresh();
			return;
		}

		int index = 0;

		for (int i = 0; i < contexts.All.Count; i++) {
			if (contexts.All[i].Id != contexts.Current?.Id) continue;

			index = i;
			break;
		}

		ProjectContext next = contexts.All[(index + 1) % contexts.All.Count];

		server.Contexts.Select(path, next.Id);
		output.Log(host, $"{Path.GetFileName(path)} is now analysed as part of {next.Label}.");

		Update(host);
		host.Refresh();
	}
}
