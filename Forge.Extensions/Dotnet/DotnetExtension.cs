using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Dotnet.Build;
using Forge.Extensions.Dotnet.Debugger;
using Forge.Extensions.Dotnet.Language;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Solution;
using Forge.Extensions.Host;

namespace Forge.Extensions.Dotnet;

public sealed class DotnetExtension : ForgeExtension
{
	private readonly DotnetSettings _settings = new();
	private readonly DotnetOutput _output = new();
	private readonly SolutionWorkspace _workspace = new();
	private readonly RoslynLanguageClient _server = new();
	private readonly ProjectContextStatus _contexts;
	private readonly ServerLifecycle _lifecycle;
	private readonly EditorCommands _editor;
	private readonly WorkspaceCommands _workspaceCommands;
	private readonly BuildTasks _build;

	public DotnetExtension() {
		DiagnosticsCoordinator diagnostics = new(_server);

		_contexts = new ProjectContextStatus(_server, _workspace, _output);
		_workspaceCommands = new WorkspaceCommands(_server, diagnostics, _output);
		_lifecycle = new ServerLifecycle(_server, _workspace, diagnostics, _contexts, _workspaceCommands, new CodeActionRunner(_server, _output), _output, _settings);
		_editor = new EditorCommands(_server, _output, _settings);
		_build = new BuildTasks(_workspace, _output) { Started = () => _server.Workspace.RefreshSourceGenerators(force: false), Finished = () => _server.Workspace.RefreshSourceGenerators(force: false) };
	}

	public override string Identifier => "forge.dotnet";

	public override string DisplayName => ".NET";

	public override ActivationEvent Activation => ActivationEvent.WorkspaceContains | ActivationEvent.FileOpened | ActivationEvent.CommandInvoked;

	public string? Target => _workspace.Target;

	protected override void Configure(Settings settings) {
		_server.Settings = settings;
		_settings.Read(settings);
	}

	public override void Register(ICommandRegistry commands) {
		DebugAdapterRegistration.Register(_settings.StorageDirectory);

		commands.Register(new Command("dotnet.build", "Build", ".NET", host => _build.Run(host, "build")));
		commands.Register(new Command("dotnet.restore", "Restore", ".NET", host => _build.Run(host, "restore")));
		commands.Register(new Command("dotnet.clean", "Clean", ".NET", host => _build.Run(host, "clean")));
		commands.Register(new Command("dotnet.explorer.buildProject", "Build Project", ".NET", host => _build.RunForActiveProject(host, "build")));
		commands.Register(new Command("dotnet.explorer.refresh", "Refresh", ".NET", Refresh));
		commands.Register(new Command("dotnet.openSolution", "Open Solution...", ".NET", OpenSolution));

		_contexts.Register(commands);
		_editor.Register(commands);
		_workspaceCommands.Register(commands);
		_lifecycle.Register(commands);
	}

	public override bool ActivatesIn(string? workspacePath) => workspacePath is not null && Directory.Exists(workspacePath) && ProjectDiscovery.ContainsProject(workspacePath, depth: 3);

	public override bool ActivatesFor(string filePath) => ProjectDiscovery.IsSource(filePath);

	public override void Deactivate() => _server.Stop();

	protected override void OnActivate(IExtensionContext context) {
		ICommandHost host = context.GetHost();

		_lifecycle.SetStatus(host, "Loading", busy: true);

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			_workspace.Reload(host.GetWorkspacePath(), _settings.SolutionPath);
			_contexts.Update(host);
			_lifecycle.Start(host);
			host.Refresh();
		}, null);
	}

	private void Refresh(ICommandHost host) {
		Reload(host);
		_contexts.Update(host);
		host.Refresh();
	}

	private void OpenSolution(ICommandHost host) {
		Reload(host);

		if (_workspace.Solution is { } solution) {
			host.OpenFile(solution);
			return;
		}

		_output.Log(host, "no solution or project in this workspace.");
		host.Refresh();
	}

	private void Reload(ICommandHost host) {
		_workspace.Reload(host.GetWorkspacePath());

		if (_workspace.Solution is { } solution) _output.Log(host, $"loaded solution {solution}");
		else if (_workspace.Projects.Length > 0) _output.Log(host, $"no solution found; loading {_workspace.Projects.Length} project(s)");
	}
}
