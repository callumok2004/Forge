using System.Diagnostics;

using Forge.Commands;
using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Debug;
using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Dotnet;

public sealed class DotnetExtension : ForgeExtension
{
	private const string BuildLoggerArguments = "-consoleLoggerParameters:NoSummary;ForceNoAlign";

	private readonly SolutionWorkspace _workspace = new();
	private readonly RoslynLanguageClient _server = new();

	private readonly Dictionary<string, IReadOnlyList<ServerDiagnostic>> _diagnostics =
		[with(StringComparer.OrdinalIgnoreCase)];

	private readonly Lock _diagnosticsSync = new();

	private IOutputChannel? _output;

	private int _acquiring;

	private void Log(ICommandHost host, string line) => (_output ??= host.Channel(".NET")).Append(line);

	public string? ServerPath { get; set; }

	public string ServerLogLevel { get; set; } = "Information";

	public bool UseServerGC { get; set; }

	public string? DotnetPath { get; set; }

	public string? SolutionPath { get; set; }

	public string StorageDirectory { get; set; } = Core.UserData.Directory;

	protected override void Configure(Settings settings) {
		_server.Settings = settings;

		ServerPath = settings.GetString("dotnet.server.path") ?? ServerPath;
		ServerLogLevel = settings.GetString("dotnet.server.logLevel") ?? ServerLogLevel;
		UseServerGC = settings.GetBoolean("dotnet.server.useServerGC") ?? UseServerGC;
		DotnetPath = settings.GetString("dotnet.dotnetPath") ?? DotnetPath;
		SolutionPath = settings.GetString("dotnet.solution.path") ?? SolutionPath;
	}

	public override string Identifier => "forge.dotnet";

	public override string DisplayName => ".NET";

	public override ActivationEvent Activation => ActivationEvent.WorkspaceContains | ActivationEvent.FileOpened | ActivationEvent.CommandInvoked;

	public string? Target => _workspace.Target;

	public override void Register(ICommandRegistry commands) {
		RegisterDebugAdapters();

		commands.Register(new Command("dotnet.build", "Build", ".NET",
			host => RunTask(host, "build")));

		commands.Register(new Command("dotnet.restore", "Restore", ".NET",
			host => RunTask(host, "restore")));

		commands.Register(new Command("dotnet.clean", "Clean", ".NET",
			host => RunTask(host, "clean")));

		commands.Register(new Command("dotnet.explorer.buildProject", "Build Project", ".NET",
			host => RunTask(host, "build", ProjectOf(host))));

		commands.Register(new Command("dotnet.explorer.refresh", "Refresh", ".NET", host => {
			Reload(host);
			UpdateProjectContext(host);
			host.Refresh();
		}));

		commands.Register(new Command("dotnet.selectProjectContext",
			"Select Project Context for Active File", ".NET", host => {
				if (host.GetActiveFilePath() is not { } path || !ActivatesFor(path)) return;

				if (_server.ContextsFor(path) is not { All.Count: > 1 } contexts) {
					Log(host, _server.ContextsFor(path)?.Current is { } only
						? $"{Path.GetFileName(path)} is analysed as part of {only.Label}."
						: $"No project context for {Path.GetFileName(path)}.");

					host.Refresh();
					return;
				}

				int index = 0;

				for (int i = 0; i < contexts.All.Count; i++) {
					if (contexts.All[i].Id == contexts.Current?.Id) {
						index = i;
						break;
					}
				}

				ProjectContext next = contexts.All[(index + 1) % contexts.All.Count];

				_server.SelectContext(path, next.Id);
				Log(host, $"{Path.GetFileName(path)} is now analysed as part of {next.Label}.");

				UpdateProjectContext(host);
				host.Refresh();
			}));

		commands.Register(new Command("dotnet.openSolution", "Open Solution...", ".NET", host => {
			Reload(host);

			if (_workspace.Solution is { } solution) {
				host.OpenFile(solution);
				return;
			}

			Log(host, "no solution or project in this workspace.");
			host.Refresh();
		}));

		commands.Register(new Command("dotnet.goToDefinition", "Go to Definition", ".NET",
			host => Navigate(host, "Definitions",
				(path, line, column) => _server.DefinitionsAsync(path, line, column))));

		commands.Register(new Command("dotnet.goToImplementation", "Go to Implementation", ".NET",
			host => Navigate(host, "Implementations",
				(path, line, column) => _server.ImplementationsAsync(path, line, column))));

		commands.Register(new Command("dotnet.findAllReferences", "Find All References", ".NET",
			host => Navigate(host, "References",
				(path, line, column) => _server.ReferencesAsync(path, line, column),
				alwaysList: true)));

		commands.Register(new Command("dotnet.formatDocument", "Format Document", ".NET",
			host => Format(host)));

		commands.Register(new Command("dotnet.showOutput", "Show Language Server Log", ".NET", host => {
			host.ShowOutput(_output ??= host.Channel(".NET"));
			host.Refresh();
		}));

		commands.Register(new Command("dotnet.acquireServer", "Install or Update Language Server",
			".NET", host => {
				host.ShowOutput(_output ??= host.Channel(".NET"));
				_server.Stop();
				AcquireThenStart(host);
				host.Refresh();
			}));

		commands.Register(new Command("dotnet.restartServer", "Restart Language Server", ".NET", host => {
			_server.Stop();
			StartServer(host);
			host.Refresh();
		}));
	}

	private void RegisterDebugAdapters() {
		string executable = NetCoreDbgAcquisition.ExecutablePath(StorageDirectory);

		foreach ((string type, string label) in ((string, string)[])[
			("coreclr", ".NET Core"),
			("clr", ".NET Framework"),
			("dotnet", ".NET")]) {
			DebugAdapters.Register(new DebugAdapterDescriptor(type, label, executable,
				["--interpreter=vscode"]) {
				Acquire = progress => NetCoreDbgAcquisition.AcquireAsync(StorageDirectory, progress),
			});
		}
	}

	public override bool ActivatesIn(string? workspacePath) {
		if (workspacePath is null || !Directory.Exists(workspacePath)) return false;

		return ContainsProject(workspacePath, depth: 3);
	}

	public override bool ActivatesFor(string filePath) =>
		filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
		filePath.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

	private static readonly string[] ProjectPatterns =
		["*.slnx", "*.sln", "*.slnf", "*.csproj", "*.fsproj", "*.vbproj"];

	private static bool ContainsProject(string directory, int depth) {
		try {
			foreach (string pattern in ProjectPatterns) {
				if (Directory.EnumerateFiles(directory, pattern).Any()) return true;
			}

			if (depth <= 0) return false;

			foreach (string child in Directory.EnumerateDirectories(directory)) {
				string name = Path.GetFileName(child);

				if (name is "bin" or "obj" or "node_modules" or ".git" or ".vs") continue;

				if (ContainsProject(child, depth - 1)) return true;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}

		return false;
	}

	public override void Deactivate() => _server.Stop();

	protected override void OnActivate(IExtensionContext context) {
		ICommandHost host = context.GetHost();

		SetStatus(host, "Loading", busy: true);

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			_workspace.Reload(host.GetWorkspacePath(), SolutionPath);

			UpdateProjectContext(host);
			StartServer(host);
			host.Refresh();
		}, null);
	}

	private void StartServer(ICommandHost host) {
		if (LanguageServerLocator.Locate(ServerPath, StorageDirectory, DotnetPath) is not { } launch) {
			if (ServerPath is { Length: > 0 }) {
				SetStatus(host, "Language server not found", busy: false, failed: true);
				Log(host, $"dotnet.server.path points at {ServerPath}, which has no " +
					"Microsoft.CodeAnalysis.LanguageServer in it.");

				return;
			}

			AcquireThenStart(host);
			return;
		}

		StartServer(host, launch);
	}

	private void AcquireThenStart(ICommandHost host) {
		if (Interlocked.Exchange(ref _acquiring, 1) == 1) return;

		SetStatus(host, "Downloading language server", busy: true);
		Log(host, "Microsoft.CodeAnalysis.LanguageServer is not installed yet.");

		_ = Task.Run(async () => {
			try {
				ServerLaunch? launch = await LanguageServerAcquisition
					.AcquireAsync(StorageDirectory, DotnetPath, line => Log(host, line))
					.ConfigureAwait(false);

				if (launch is null) SetStatus(host, "Language server not available", busy: false, failed: true);
				else StartServer(host, launch);
			}
			finally {
				Interlocked.Exchange(ref _acquiring, 0);
				host.Refresh();
			}
		});
	}

	private void StartServer(ICommandHost host, ServerLaunch launch) {
		_server.Log += message => Log(host, message);

		_server.StateChanged += () => {
			SetStatus(host, _server.StatusText, _server.State == LanguageServerState.Starting,
				_server.State == LanguageServerState.Failed);

			host.Refresh();
		};

		_server.DiagnosticsPublished += (file, diagnostics) => {
			lock (_diagnosticsSync) {
				if (diagnostics.Count == 0) _diagnostics.Remove(file);
				else _diagnostics[file] = diagnostics;
			}

			host.ReportProblems(AllDiagnostics());
			host.Refresh();
		};

		host.ProvideLanguageFeatures(new RoslynFeatures(this));

		host.ObserveDocuments((kind, path, text) => {
			if (!ActivatesFor(path)) return;

			switch (kind) {
				case DocumentEvent.Opened:
					_server.DidOpen(path, text ?? string.Empty);

					UpdateProjectContext(host);
					RefreshDiagnostics(host, path, immediate: true);
					break;

				case DocumentEvent.Changed:
					_server.DidChange(path, text ?? string.Empty);
					RefreshDiagnostics(host, path);
					break;

				case DocumentEvent.Saved:
					_server.DidSave(path);
					RefreshDiagnostics(host, path, immediate: true);
					break;

				case DocumentEvent.Closed:
					_server.DidClose(path);
					break;
			}
		});

		_server.ProjectsLoaded += () => {
			_server.InvalidateContexts();
			UpdateProjectContext(host);

			if (host.GetActiveFilePath() is { } open && ActivatesFor(open)) {
				RefreshDiagnostics(host, open, immediate: true);
			}

			host.Refresh();
		};

		_server.Start(launch, Path.Combine(StorageDirectory, "logs"), _workspace.Solution,
			_workspace.Projects, ServerLogLevel, UseServerGC);
	}

	private sealed class RoslynFeatures(DotnetExtension owner) : ILanguageFeatures
	{
		public bool Handles(string path) => owner.ActivatesFor(path);

		public async Task<CompletionList> CompletionsAsync(string path, int line, int column,
			char trigger, CompletionTrigger kind) {
			ServerCompletionList list = await owner._server
				.CompletionAsync(path, line, column, trigger, (int)kind).ConfigureAwait(false);

			IReadOnlyList<ServerCompletion> items = list.Items;
			CompletionEntry[] entries = new CompletionEntry[items.Count];

			for (int i = 0; i < items.Count; i++) {
				ServerCompletion item = items[i];

				entries[i] = new CompletionEntry(item.Index, item.Label, item.LabelDetail,
					item.LabelDescription, item.InsertText, item.Detail, item.Kind, item.FilterText,
					item.Preselect, item.ReplaceLine, item.ReplaceStartColumn, item.ReplaceEndColumn);
			}

			return new CompletionList(entries, list.IsIncomplete);
		}

		public async Task<CompletionDetail?> ResolveCompletionAsync(int index) {
			if (await owner._server.ResolveCompletionAsync(index).ConfigureAwait(false)
				is not { } detail) return null;

			HostTextEdit[] edits = new HostTextEdit[detail.AdditionalEdits.Count];

			for (int i = 0; i < edits.Length; i++) {
				ServerTextEdit edit = detail.AdditionalEdits[i];

				edits[i] = new HostTextEdit(edit.StartLine, edit.StartColumn, edit.EndLine,
					edit.EndColumn, edit.NewText);
			}

			return new CompletionDetail(detail.Detail, detail.Documentation, edits);
		}

		public async Task<SignatureHelp?> SignatureHelpAsync(string path, int line, int column,
			char trigger) {
			if (await owner._server.SignatureHelpAsync(path, line, column, trigger)
				.ConfigureAwait(false) is not { } help) return null;

			ServerSignature active = help.Active;

			return new SignatureHelp(active.Label, active.Parameters, active.Documentation,
				help.ParameterIndex, help.ActiveIndex, help.Overloads.Count);
		}

		public Task<string?> HoverAsync(string path, int line, int column) =>
			owner._server.HoverAsync(path, line, column);
	}

	private void Navigate(ICommandHost host, string title,
		Func<string, int, int, Task<IReadOnlyList<ServerLocation>>> request, bool alwaysList = false) {
		if (host.GetActiveFilePath() is not { } path || !ActivatesFor(path)) return;

		int line = host.GetCaretLine();
		int column = host.GetCaretColumn();

		_ = Task.Run(async () => {
			IReadOnlyList<ServerLocation> found = await request(path, line, column).ConfigureAwait(false);

			if (found.Count == 0) {
				Log(host, $"{title}: nothing found at {Path.GetFileName(path)}:{line}.");
				host.ShowReferences(title, []);
			}
			else if (found.Count == 1 && !alwaysList) {
				ServerLocation only = found[0];
				host.GoTo(only.Path, only.Line, only.Column);
			}
			else {
				host.ShowReferences(title, [.. found.Select(location =>
					new HostLocation(location.Path, location.Line, location.Column, location.Preview))]);
			}

			host.Refresh();
		});
	}

	private void Format(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path || !ActivatesFor(path)) return;

		_ = Task.Run(async () => {
			IReadOnlyList<ServerTextEdit> edits = await _server
				.FormatAsync(path, tabSize: 4, insertSpaces: false).ConfigureAwait(false);

			if (edits.Count == 0) {
				Log(host, "Format Document: nothing to change.");
				return;
			}

			host.EditActiveDocument(text => ApplyEdits(text, edits));
			host.Refresh();
		});
	}

	private static string ApplyEdits(string text, IReadOnlyList<ServerTextEdit> edits) {
		List<int> starts = [0];

		for (int i = 0; i < text.Length; i++) {
			if (text[i] == '\n') starts.Add(i + 1);
		}

		System.Text.StringBuilder builder = new(text);

		foreach (ServerTextEdit edit in edits) {
			int start = Offset(edit.StartLine, edit.StartColumn);
			int end = Offset(edit.EndLine, edit.EndColumn);

			if (start < 0 || end < start || end > builder.Length) continue;

			builder.Remove(start, end - start);
			builder.Insert(start, edit.NewText);
		}

		return builder.ToString();

		int Offset(int line, int column) {
			int index = line - 1;

			return index < 0 || index >= starts.Count
				? -1
				: Math.Min(starts[index] + column - 1, text.Length);
		}
	}

	private readonly Dictionary<string, long> _diagnosticsDue = [with(StringComparer.OrdinalIgnoreCase)];

	private void RefreshDiagnostics(ICommandHost host, string path, bool immediate = false) {
		long due = Environment.TickCount64 + (immediate ? 0 : 300);

		lock (_diagnosticsSync) _diagnosticsDue[path] = due;

		_ = Task.Run(async () => {
			if (!immediate) {
				await Task.Delay(300).ConfigureAwait(false);

				lock (_diagnosticsSync) {
					if (_diagnosticsDue.TryGetValue(path, out long latest) && latest != due) return;
				}
			}

			await _server.RequestDiagnosticsAsync(path).ConfigureAwait(false);
			await RefreshSemanticTokensAsync(host, path).ConfigureAwait(false);
		});
	}

	private async Task RefreshSemanticTokensAsync(ICommandHost host, string path) {
		IReadOnlyList<ServerToken> tokens = await _server.SemanticTokensAsync(path)
			.ConfigureAwait(false);

		if (tokens.Count == 0) return;

		List<SemanticToken> coloured = [with(tokens.Count)];

		foreach (ServerToken token in tokens) {
			if (Syntax.SemanticColors.For(token.Type) is not { } colour) continue;

			coloured.Add(new SemanticToken(token.Line, token.StartColumn, token.Length,
				((uint)(colour.A * 255f) << 24) | ((uint)(colour.R * 255f) << 16) |
				((uint)(colour.G * 255f) << 8) | (uint)(colour.B * 255f)));
		}

		host.SetSemanticTokens(path, coloured);
		host.Refresh();
	}

	private Diagnostic[] AllDiagnostics() {
		lock (_diagnosticsSync) {
			return [.. _diagnostics.Values
				.SelectMany(static file => file)
				.OrderByDescending(static diagnostic => diagnostic.IsError)
				.Select(static diagnostic => new Diagnostic(diagnostic.File, diagnostic.Line,
					diagnostic.Column, diagnostic.Message, diagnostic.IsError))];
		}
	}

	public RoslynLanguageClient Server => _server;

	private void SetStatus(ICommandHost host, string state, bool busy, bool failed = false) {
		string icon = busy ? "Loading" : failed ? "Error" : "Check";

		host.SetStatusItem("dotnet.status", icon, ".NET", "dotnet.showOutput");
		Log(host, state);
	}

	private void UpdateProjectContext(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path || !ActivatesFor(path)) {
			host.RemoveStatusItem("dotnet.projectContext");
			return;
		}

		_ = Task.Run(async () => {
			if (await _server.ProjectContextsAsync(path).ConfigureAwait(false) is { Current: { } current }) {
				string label = _contextsCount(path) > 1
					? $"{current.Label} (+{_contextsCount(path) - 1})"
					: current.Label;

				host.SetStatusItem("dotnet.projectContext", "Project", label,
					"dotnet.selectProjectContext", rightAligned: true, priority: 100);

				host.Refresh();
				return;
			}

			if (_workspace.Target is { } target) {
				host.SetStatusItem("dotnet.projectContext", "Project",
					Path.GetFileNameWithoutExtension(target), "dotnet.selectProjectContext",
					rightAligned: true, priority: 100);
			}
			else host.RemoveStatusItem("dotnet.projectContext");

			host.Refresh();
		});
	}

	private int _contextsCount(string path) => _server.ContextsFor(path)?.All.Count ?? 0;

	private void Reload(ICommandHost host) {
		_workspace.Reload(host.GetWorkspacePath());

		if (_workspace.Solution is { } solution) Log(host, $"loaded solution {solution}");
		else if (_workspace.Projects.Length > 0) {
			Log(host, $"no solution found; loading {_workspace.Projects.Length} project(s)");
		}
	}

	private void RunTask(ICommandHost host, string command, string? project = null) {
		if (project is null && _workspace.Target is null) Reload(host);

		if ((project ?? _workspace.Target) is not { } target) {
			Log(host, "no solution or project in this workspace.");
			host.Refresh();
			return;
		}

		string arguments = command == "build"
			? $"{command} \"{target}\" {BuildLoggerArguments}"
			: $"{command} \"{target}\"";

		Log(host, $"{command} {Path.GetFileName(target)}");

		MsBuildProblemMatcher matcher = new();
		long started = Stopwatch.GetTimestamp();
		AmbientSignal signal = command == "test" ? AmbientSignal.Tests : AmbientSignal.Build;

		Ambient.Begin(signal);

		host.RunInTerminal($"dotnet {command}", $"dotnet {arguments}", Path.GetDirectoryName(target),
			line => {
				if (matcher.Read(line)) host.ReportProblems(matcher.Diagnostics);

				host.Refresh();
			},
			exitCode => {
				Ambient.End(signal, exitCode == 0);

				TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

				Log(host, exitCode == 0
					? $".NET: {command} succeeded in {elapsed.TotalSeconds:F1}s"
					: $".NET: {command} failed with exit code {exitCode} after {elapsed.TotalSeconds:F1}s");

				if (matcher.Diagnostics.Count > 0) {
					Log(host, $"{matcher.ErrorCount} error(s), {matcher.WarningCount} warning(s)");

					if (exitCode != 0) host.ShowPanel("Problems");
				}
				else if (exitCode == 0) host.ReportProblems([]);

				host.Refresh();
			});
	}

	private static string? ProjectOf(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } active) return null;

		for (string? current = Path.GetDirectoryName(active); current is not null;
			current = Path.GetDirectoryName(current)) {
			try {
				foreach (string pattern in (string[])["*.csproj", "*.fsproj", "*.vbproj"]) {
					string[] matches = Directory.GetFiles(current, pattern);

					if (matches.Length > 0) {
						Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
						return matches[0];
					}
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			}
		}

		return null;
	}
}
