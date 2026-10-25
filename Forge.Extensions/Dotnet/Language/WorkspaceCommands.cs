using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Server.Features;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Extensions.Dotnet.Language;

public sealed class WorkspaceCommands(RoslynLanguageClient server, DiagnosticsCoordinator diagnostics, DotnetOutput output)
{
	private ICommandRegistry? _commands;

	public void Register(ICommandRegistry commands) {
		_commands = commands;

		commands.Register(new Command("dotnet.restore.project", "Restore Project", ".NET", RestoreProject));
		commands.Register(new Command("dotnet.restore.all", "Restore All Projects", ".NET", host => Restore(host, [])));
		commands.Register(new Command("csharp.rerunSourceGenerators", "Rerun Source Generators", "C#", _ => server.Workspace.RefreshSourceGenerators(force: true)));
	}

	public void Attach(ICommandHost host) {
		server.Workspace.ApplyEdit = host.ApplyWorkspaceEditAsync;
		server.Workspace.RestoreProgress += line => output.Log(host, $"[.NET Restore] {line}");
		server.Workspace.Toast += (type, message, actions) => ShowToast(host, type, message, actions);
		server.Workspace.DiagnosticsRefreshRequested += () => RefreshActive(host);
		server.Workspace.SemanticTokensRefreshRequested += () => RefreshActive(host);
	}

	private void RefreshActive(ICommandHost host) {
		if (host.GetActiveFilePath() is { } path && ProjectDiscovery.IsSource(path)) diagnostics.Refresh(path);
	}

	private void RestoreProject(ICommandHost host) => _ = Task.Run(async () => {
		IReadOnlyList<string> projects = await server.Workspace.RestorableProjectsAsync().ConfigureAwait(false);

		if (projects.Count == 0) {
			host.ShowMessage(MessageSeverity.Info, "No projects need restoring.", []);
			return;
		}

		host.ShowQuickPick("Select a project to restore", [.. projects.Select(static project => new QuickPickItem($"Restore {Path.GetFileName(project)}", project))], index => {
			if (index >= 0) Restore(host, [projects[index]]);
		});
	});

	private void Restore(ICommandHost host, IReadOnlyList<string> projects) {
		if (server.Workspace.IsRestoring) {
			host.ShowMessage(MessageSeverity.Error, "Restore already in progress", []);
			return;
		}

		output.Show(host);

		_ = Task.Run(async () => {
			if (!await server.Workspace.RestoreAsync(projects).ConfigureAwait(false)) output.Log(host, "[.NET Restore] failed");

			host.Refresh();
		});
	}

	private void ShowToast(ICommandHost host, int type, string message, IReadOnlyList<ServerCommand> actions) {
		MessageSeverity severity = type switch {
			1 => MessageSeverity.Error,
			2 => MessageSeverity.Warning,
			_ => MessageSeverity.Info,
		};

		host.ShowMessage(severity, message, [.. actions.Select(static action => action.Title)], index => {
			if (index < 0) return;

			ServerCommand action = actions[index];

			if (_commands?.Find(action.Command) is not null) _commands.Execute(action.Command, host);
			else _ = server.Workspace.ExecuteAsync(action);
		});
	}
}
