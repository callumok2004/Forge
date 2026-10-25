using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Forge.Core.Native;
using Forge.Extensions.Dotnet.Server.Features;
using Forge.Language;

namespace Forge.Extensions.Dotnet.Server;

public sealed partial class RoslynLanguageClient : IDisposable
{
	private static readonly Dictionary<string, HashSet<string>> OptionCache = [with(StringComparer.OrdinalIgnoreCase)];

	private JsonRpcConnection? _connection;
	private string? _solutionPath;
	private IReadOnlyList<string> _projectPaths = [];

	public RoslynLanguageClient() {
		Contexts = new ProjectContextStore(this);
		Documents = new DocumentSync(this);
		Diagnostics = new DiagnosticRequests(this);
		Completion = new CompletionRequests(this);
		Signatures = new SignatureHelpRequests(this);
		Navigation = new NavigationRequests(this);
		Formatting = new FormattingRequests(this);
		SemanticTokens = new SemanticTokenRequests(this);
		Hover = new HoverRequests(this);
		Files = new WatchedFiles(this);
		Generated = new SourceGeneratedDocuments(this);
		Workspace = new WorkspaceRequests(this);
		Typing = new TypingRequests(this);
		Highlights = new HighlightRequests(this);
		Rename = new RenameRequests(this);
		CodeActions = new CodeActionRequests(this);
	}

	public CodeActionRequests CodeActions { get; }

	public RenameRequests Rename { get; }

	public HighlightRequests Highlights { get; }

	public TypingRequests Typing { get; }

	public WorkspaceRequests Workspace { get; }

	public SourceGeneratedDocuments Generated { get; }

	internal string UriOf(string path) => Generated.UriFor(path) ?? ToUri(path);

	internal async Task<string?> PathOfAsync(string uri) => SourceGeneratedDocuments.IsGenerated(uri) ? await Generated.MaterialiseAsync(uri).ConfigureAwait(false) : FromUri(uri);

	public WatchedFiles Files { get; }

	public ProjectContextStore Contexts { get; }

	public DocumentSync Documents { get; }

	public DiagnosticRequests Diagnostics { get; }

	public CompletionRequests Completion { get; }

	public SignatureHelpRequests Signatures { get; }

	public NavigationRequests Navigation { get; }

	public FormattingRequests Formatting { get; }

	public SemanticTokenRequests SemanticTokens { get; }

	public HoverRequests Hover { get; }

	public LanguageServerState State { get; private set; } = LanguageServerState.Stopped;

	public string StatusText { get; private set; } = "Stopped";

	public Core.Settings? Settings { get; set; }

	public event Action? StateChanged;

	public event Action<string>? Log;

	public event Action? ProjectsLoaded;

	internal JsonRpcConnection? Connection => _connection is { IsRunning: true } connection ? connection : null;

	internal async Task<JsonNode?> RequestAsync(string method, JsonObject parameters) {
		if (Connection is not { } connection) return null;

		try {
			return await connection.SendRequestAsync(method, parameters).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return null;
		}
	}

	internal void Notify(string method, JsonObject parameters) => Connection?.SendNotification(method, parameters);

	internal JsonObject DocumentAt(string path, int line, int column) => new() {
		["textDocument"] = Contexts.Identifier(path),
		["position"] = LspJson.Position(line, column),
	};

	public void Start(ServerLaunch launch, string logDirectory, string? solutionPath, IReadOnlyList<string> projectPaths, DotnetSettings settings) {
		Stop();
		SetState(LanguageServerState.Starting, "Starting...");

		HashSet<string> supported = SupportedOptions(launch);
		List<string> arguments = [.. launch.Arguments];

		Add("--stdio");
		arguments.AddRange(["--logLevel", settings.ServerLogLevel, "--extensionLogDirectory", logDirectory]);
		Add("--telemetryLevel", "off");
		Add("--sourceGeneratorExecutionPreference", settings.SourceGeneratorExecution);
		Add($"--clientProcessId={Environment.ProcessId}");

		if (supported.Contains("--csharpDesignTimePath")) {
			if (LanguageServerLocator.DesignTimeTargets(launch.ServerDirectory) is { } targets) arguments.AddRange(["--csharpDesignTimePath", targets]);
			else Log?.Invoke($"Design time targets missing next to {launch.ServerDirectory}; project loading will be degraded.");
		}

		void Add(string option, string? value = null) {
			int equals = option.IndexOf('=');
			string name = equals < 0 ? option : option[..equals];

			if (!supported.Contains(name)) return;

			arguments.Add(option);

			if (value is not null) arguments.Add(value);
		}

		ProcessStartInfo startInfo = new() {
			FileName = launch.Command,
			WorkingDirectory = launch.ServerDirectory,
		};

		foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

		startInfo.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";

		if (!settings.UseServerGC) {
			startInfo.Environment["DOTNET_gcServer"] = "0";
			startInfo.Environment["DOTNET_gcConcurrent"] = "1";
			startInfo.Environment["DOTNET_GCConserveMemory"] = "9";
			startInfo.Environment["DOTNET_GCRetainVM"] = "0";
		}

		foreach ((string name, string value) in settings.ServerEnvironment) startInfo.Environment[name] = value;

		Log?.Invoke($"Starting {launch.Command} {string.Join(' ', arguments)}");

		try {
			Directory.CreateDirectory(logDirectory);

			JsonRpcConnection connection = new(startInfo);
			connection.Log += message => Log?.Invoke(message);
			connection.Notification += OnNotification;
			connection.RequestHandler = (method, parameters) => method switch {
				"workspace/configuration" => LanguageServerOptions.Resolve(parameters, Settings),
				_ => Workspace.OnServerRequest(method),
			};
			connection.AsyncRequestHandler = (method, parameters) => method == "workspace/applyEdit" ? Workspace.OnApplyEditAsync(parameters) : null;

			_connection = connection;

			Initialize(connection, solutionPath, projectPaths);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) {
			SetState(LanguageServerState.Failed, "Failed to start");
			Log?.Invoke($"Language server failed to start: {exception.Message}");
		}
	}

	private static HashSet<string> SupportedOptions(ServerLaunch launch) {
		lock (OptionCache) {
			if (OptionCache.TryGetValue(launch.ServerDirectory, out HashSet<string>? cached)) return cached;
		}

		HashSet<string> options = [with(StringComparer.Ordinal)];

		try {
			ProcessStartInfo startInfo = new() {
				FileName = launch.Command,
				WorkingDirectory = launch.ServerDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			foreach (string argument in launch.Arguments) startInfo.ArgumentList.Add(argument);

			startInfo.ArgumentList.Add("--help");

			using Process? help = ProcessGroup.Start(startInfo);

			if (help is not null) {
				string text = help.StandardOutput.ReadToEnd();
				help.WaitForExit(30_000);

				foreach (Match match in OptionPattern().Matches(text)) options.Add(match.Value);
			}
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException) {
		}

		lock (OptionCache) OptionCache[launch.ServerDirectory] = options;

		return options;
	}

	private void Initialize(JsonRpcConnection connection, string? solutionPath, IReadOnlyList<string> projectPaths) {
		JsonObject parameters = new() {
			["processId"] = Environment.ProcessId,
			["clientInfo"] = new JsonObject { ["name"] = "Forge", ["version"] = "0.1" },
			["capabilities"] = ClientCapabilities.Create(),
			["rootUri"] = null,
			["initializationOptions"] = new JsonObject {
				["completionInsertsUnimportedNamespaces"] = true,
			},
		};

		Task.Run(async () => {
			try {
				Task<JsonNode?> handshake = connection.SendRequestAsync("initialize", parameters);

				if (await Task.WhenAny(handshake, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) != handshake) {
					SetState(LanguageServerState.Failed, "Initialisation timed out");
					Log?.Invoke("The language server did not answer initialize within 60 seconds.");
					return;
				}

				JsonNode? result = await handshake.ConfigureAwait(false);

				if (result?["capabilities"] is JsonObject served) {
					Log?.Invoke($"Server capabilities: {string.Join(", ", served.Select(pair => pair.Key))}");
					SemanticTokens.ReadLegend(served["semanticTokensProvider"]?["legend"]);
					Typing.ReadCapabilities(served);
				}

				connection.SendNotification("initialized", new JsonObject());
				Documents.Ready();

				SetState(LanguageServerState.Running, "Loading projects...");

				_solutionPath = solutionPath;
				_projectPaths = projectPaths;

				if (!OpenWorkspace()) SetState(LanguageServerState.Running, "Ready");
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException) {
				SetState(LanguageServerState.Failed, "Initialisation failed");
				Log?.Invoke($"Language server initialisation failed: {exception.Message}");
			}
		});
	}

	public bool OpenWorkspace() {
		if (_solutionPath is { Length: > 0 } solution) {
			Log?.Invoke($"Opening solution {solution}");
			Notify("solution/open", new JsonObject { ["solution"] = solution });
			return true;
		}

		if (_projectPaths.Count == 0) return false;

		Log?.Invoke($"Opening {_projectPaths.Count} project(s)");

		JsonArray projects = [];

		foreach (string project in _projectPaths) projects.Add(project);

		Notify("project/open", new JsonObject { ["projects"] = projects });
		return true;
	}

	private void OnNotification(string method, JsonNode? parameters) {
		switch (method) {
			case "textDocument/publishDiagnostics":
				Diagnostics.OnPublished(parameters);
				break;

			case "workspace/projectInitializationComplete":
				SetState(LanguageServerState.Running, "Ready");
				ProjectsLoaded?.Invoke();
				break;

			case "$/progress":
				Workspace.OnProgress(parameters);
				break;

			case "window/_roslyn_showToast":
				Workspace.OnToast(parameters);
				break;

			case "window/logMessage":
			case "window/showMessage":
				if (parameters?["message"]?.GetValue<string>() is { Length: > 0 } message) Log?.Invoke(message);

				break;
		}
	}

	public void Stop() {
		_connection?.Dispose();
		_connection = null;
		Documents.Reset();
		SetState(LanguageServerState.Stopped, "Stopped");
	}

	private void SetState(LanguageServerState state, string text) {
		State = state;
		StatusText = text;
		StateChanged?.Invoke();
	}

	public static string ToUri(string path) {
		StringBuilder builder = new("file:///", path.Length + 16);

		foreach (char character in path) {
			_ = character switch {
				'\\' => builder.Append('/'),
				'%' => builder.Append("%25"),
				' ' => builder.Append("%20"),
				'#' => builder.Append("%23"),
				'?' => builder.Append("%3F"),
				_ => builder.Append(character),
			};
		}

		return builder.ToString();
	}

	public static string FromUri(string uri) {
		string path = uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? uri[8..] : uri;

		return Uri.UnescapeDataString(path).Replace('/', '\\');
	}

	public void Dispose() {
		Stop();
		Files.Dispose();
	}

	[GeneratedRegex(@"--[A-Za-z][A-Za-z0-9-]*")]
	private static partial Regex OptionPattern();
}
