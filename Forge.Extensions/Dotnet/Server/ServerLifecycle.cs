using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.Dotnet.Language;
using Forge.Extensions.Dotnet.Layout;
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

	private bool _updateChecked;

	private void CheckForUpdate(ICommandHost host) {
		if (_updateChecked || settings.ServerPath is { Length: > 0 }) return;

		_updateChecked = true;

		string snooze = Path.Combine(settings.StorageDirectory, "roslyn-update-snoozed");

		if (File.Exists(snooze) && DateTime.UtcNow - File.GetLastWriteTimeUtc(snooze) < TimeSpan.FromHours(24)) return;

		_ = Task.Run(async () => {
			string? installed = LanguageServerAcquisition.InstalledVersion(settings.StorageDirectory);

			if (await LanguageServerAcquisition.NewestAsync().ConfigureAwait(false) is not { } newest || !LanguageServerAcquisition.IsNewer(newest.Version, installed)) return;

			output.Log(host, $"A newer language server is available: {newest.Version} (installed: {installed ?? "none"}).");
			host.ShowMessage(MessageSeverity.Info, $"A newer C# language server is available ({newest.Version}). You have {installed ?? "none"}.", ["Update", "Later"], choice => {
				if (choice != 0) {
					try {
						Directory.CreateDirectory(settings.StorageDirectory);
						File.WriteAllText(snooze, string.Empty);
						File.SetLastWriteTimeUtc(snooze, DateTime.UtcNow);
					}
					catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
					}

					return;
				}

				output.Show(host);
				server.Stop();
				AcquireThenStart(host);
				host.Refresh();
			});
		});
	}

	private void Start(ICommandHost host, ServerLaunch launch) {
		if (!_attached) Attach(host);

		CheckForUpdate(host);

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
		TypeLayoutFeature layout = CreateLayout(host);
		server.ProjectsLoaded += layout.Refresh;
		host.ProvideLanguageFeatures(new RoslynFeatures(server, settings, actions, host, layout));
		host.ObserveDocuments((kind, path, text) => OnDocument(host, kind, path, text));
	}

	private TypeLayoutFeature CreateLayout(ICommandHost host) {
		string root = host.GetWorkspacePath() is { Length: > 0 } workspacePath ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath)) : string.Empty;

		LayoutEngine engine = new(Definition, Text, InWorkspace);

		return new TypeLayoutFeature(engine, settings, host.Refresh);

		async Task<DefinitionTarget?> Definition(string path, int line, int column) {
			IReadOnlyList<ServerLocation> found = await server.Navigation.DefinitionsAsync(path, line, column).ConfigureAwait(false);

			return found.Count == 0 ? null : new DefinitionTarget(found[0].Path, found[0].Line, found[0].Column);
		}

		string? Text(string path) {
			try {
				return host.GetDocumentText(path);
			}
			catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException) {
				return null;
			}
		}

		bool InWorkspace(string path) => root.Length > 0 && path.Length > root.Length && Path.IsPathRooted(path) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] is '\\' or '/';
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
