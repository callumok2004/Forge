using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.Dotnet.Language;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Extensions.Dotnet.Server;

public sealed class ServerLifecycle(RoslynLanguageClient server, SolutionWorkspace workspace, DiagnosticsCoordinator diagnostics, ProjectContextStatus contexts, WorkspaceCommands workspaceCommands, CodeActionRunner actions, DotnetOutput output, DotnetSettings settings)
{
	private int _acquiring;
	private bool _attached;

	public void Register(ICommandRegistry commands) {
		actions.Commands = commands;

		commands.Register(new Command("dotnet.showOutput", "Show Language Server Log", ".NET", host => {
			output.Show(host);
			host.Refresh();
		}));

		commands.Register(new Command("dotnet.acquireServer", "Install or Update Language Server", ".NET", host => {
			output.Show(host);
			server.Stop();
			AcquireThenStart(host);
			host.Refresh();
		}));

		commands.Register(new Command("dotnet.restartServer", "Restart Language Server", ".NET", host => {
			server.Stop();
			Start(host);
			host.Refresh();
		}));
	}

	public void Start(ICommandHost host) {
		if (LanguageServerLocator.Locate(settings.ServerPath, settings.StorageDirectory, settings.DotnetPath) is { } launch) {
			Start(host, launch);
			return;
		}

		if (settings.ServerPath is not { Length: > 0 }) {
			AcquireThenStart(host);
			return;
		}

		SetStatus(host, "Language server not found", busy: false, failed: true);
		output.Log(host, $"dotnet.server.path points at {settings.ServerPath}, which has no Microsoft.CodeAnalysis.LanguageServer in it.");
	}

	public void SetStatus(ICommandHost host, string state, bool busy, bool failed = false) {
		host.SetStatusItem("dotnet.status", busy ? "Loading" : failed ? "Error" : "Check", ".NET", "dotnet.showOutput");
		output.Log(host, state);
	}

	private void AcquireThenStart(ICommandHost host) {
		if (Interlocked.Exchange(ref _acquiring, 1) == 1) return;

		SetStatus(host, "Downloading language server", busy: true);
		output.Log(host, "Microsoft.CodeAnalysis.LanguageServer is not installed yet.");

		_ = Task.Run(async () => {
			try {
				ServerLaunch? launch = await LanguageServerAcquisition.AcquireAsync(settings.StorageDirectory, settings.DotnetPath, line => output.Log(host, line)).ConfigureAwait(false);

				if (launch is null) SetStatus(host, "Language server not available", busy: false, failed: true);
				else Start(host, launch);
			}
			finally {
				Interlocked.Exchange(ref _acquiring, 0);
				host.Refresh();
			}
		});
	}

	private void Start(ICommandHost host, ServerLaunch launch) {
		if (!_attached) Attach(host);

		server.Start(launch, Path.Combine(settings.StorageDirectory, "logs"), workspace.Solution, workspace.Projects, settings);
	}

	private void Attach(ICommandHost host) {
		_attached = true;

		server.Log += message => output.Log(host, message);

		server.StateChanged += () => {
			SetStatus(host, server.StatusText, server.State == LanguageServerState.Starting, server.State == LanguageServerState.Failed);
			host.Refresh();
		};

		server.ProjectsLoaded += () => {
			server.Contexts.Invalidate();
			contexts.Update(host);

			if (host.GetActiveFilePath() is { } open && ProjectDiscovery.IsSource(open)) diagnostics.Refresh(open, immediate: true);

			host.Refresh();
		};

		server.Files.ProjectsReopened += () => {
			contexts.Update(host);

			if (host.GetActiveFilePath() is { } open && ProjectDiscovery.IsSource(open)) diagnostics.Refresh(open, immediate: true);
		};

		server.Files.Watch(host.GetWorkspacePath());
		diagnostics.Attach(host);
		workspaceCommands.Attach(host);
		server.CodeLenses.Changed += host.Refresh;
		host.ProvideLanguageFeatures(new RoslynFeatures(server, settings, actions, host));
		host.ObserveDocuments((kind, path, text) => OnDocument(host, kind, path, text));
	}

	private void OnDocument(ICommandHost host, DocumentEvent kind, string path, string? text) {
		if (!ProjectDiscovery.IsSource(path)) return;

		switch (kind) {
			case DocumentEvent.Opened:
				server.Documents.Open(path, text ?? string.Empty);
				contexts.Update(host);
				diagnostics.Refresh(path, immediate: true);
				break;

			case DocumentEvent.Changed:
				server.Documents.Change(path, text ?? string.Empty);
				diagnostics.Refresh(path);
				break;

			case DocumentEvent.Saved:
				server.Documents.Save(path);
				diagnostics.Refresh(path, immediate: true);
				break;

			case DocumentEvent.Closed:
				server.Documents.Close(path);
				break;
		}
	}
}
