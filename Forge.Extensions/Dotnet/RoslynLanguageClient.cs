using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Forge.Common.Extensions;
using Forge.Core.Native;
using Forge.Core;
using Forge.Extensions.Host;
using Forge.Language;

namespace Forge.Extensions.Dotnet;

public readonly record struct ServerLocation(string Path, int Line, int Column, string Preview);

public readonly record struct ServerSymbol(string Name, int Kind, int Depth, int Line, int Column);

public readonly record struct ServerTextEdit(int StartLine, int StartColumn, int EndLine,
	int EndColumn, string NewText);

public readonly record struct ProjectContext(string Id, string Label, bool IsMiscellaneous);

public sealed record ProjectContexts(IReadOnlyList<ProjectContext> All, int DefaultIndex)
{
	public string? SelectedId { get; init; }

	public ProjectContext? Current {
		get {
			foreach (ProjectContext context in All) {
				if (context.Id == SelectedId) return context;
			}

			return DefaultIndex < All.Count ? All[DefaultIndex] : null;
		}
	}
}

public readonly record struct ServerToken(int Line, int StartColumn, int Length, string Type);

public readonly record struct ServerCompletion(int Index, string Label, string LabelDetail,
	string LabelDescription, string InsertText, string Detail, string Kind, string SortText,
	string FilterText, bool Preselect, int ReplaceLine, int ReplaceStartColumn, int ReplaceEndColumn);

public readonly record struct ServerCompletionList(IReadOnlyList<ServerCompletion> Items,
	bool IsIncomplete);

public readonly record struct ServerCompletionDetail(string Detail, string Documentation,
	IReadOnlyList<ServerTextEdit> AdditionalEdits);

public readonly record struct ServerSignature(string Label,
	IReadOnlyList<(int Start, int Length)> Parameters, string Documentation, int ActiveParameter);

public sealed record ServerSignatures(IReadOnlyList<ServerSignature> Overloads, int ActiveIndex,
	int ActiveParameter)
{
	public ServerSignature Active => Overloads[Math.Clamp(ActiveIndex, 0, Overloads.Count - 1)];

	public int ParameterIndex => Active.ActiveParameter >= 0 ? Active.ActiveParameter : ActiveParameter;
}

public readonly record struct ServerDiagnostic(string File, int Line, int Column, string Message,
	bool IsError);

public enum LanguageServerState
{
	Stopped,
	Starting,
	Running,
	Failed,
}

public sealed partial class RoslynLanguageClient : IDisposable
{
	private readonly Dictionary<string, int> _versions = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	private JsonRpcConnection? _connection;

	public LanguageServerState State { get; private set; } = LanguageServerState.Stopped;

	public string StatusText { get; private set; } = "Stopped";

	public event Action? StateChanged;

	public event Action<string>? Log;

	public event Action<string, IReadOnlyList<ServerDiagnostic>>? DiagnosticsPublished;

	public event Action? ProjectsLoaded;

	private static readonly Dictionary<string, HashSet<string>> OptionCache =
		[with(StringComparer.OrdinalIgnoreCase)];

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

				foreach (Match match in MyRegex().Matches(text)) options.Add(match.Value);
			}
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException) {
		}

		lock (OptionCache) OptionCache[launch.ServerDirectory] = options;

		return options;
	}

	public Core.Settings? Settings { get; set; }

	public void Start(ServerLaunch launch, string logDirectory, string? solutionPath,
		IReadOnlyList<string> projectPaths, string logLevel = "Information", bool useServerGC = false) {
		Stop();
		SetState(LanguageServerState.Starting, "Starting...");

		HashSet<string> supported = SupportedOptions(launch);

		List<string> arguments = [.. launch.Arguments];

		Add("--stdio");

		arguments.AddRange(["--logLevel", logLevel, "--extensionLogDirectory", logDirectory]);

		Add("--telemetryLevel", "off");
		Add("--sourceGeneratorExecutionPreference", "automatic");
		Add($"--clientProcessId={Environment.ProcessId}");

		if (supported.Contains("--csharpDesignTimePath")) {
			if (LanguageServerLocator.DesignTimeTargets(launch.ServerDirectory) is { } targets) {
				arguments.AddRange(["--csharpDesignTimePath", targets]);
			}
			else {
				Log?.Invoke($"Design time targets missing next to {launch.ServerDirectory}; " +
					"project loading will be degraded.");
			}
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

		if (!useServerGC) {
			startInfo.Environment["DOTNET_gcServer"] = "0";
			startInfo.Environment["DOTNET_gcConcurrent"] = "1";
			startInfo.Environment["DOTNET_GCConserveMemory"] = "9";
			startInfo.Environment["DOTNET_GCRetainVM"] = "0";
		}

		Log?.Invoke($"Starting {launch.Command} {string.Join(' ', arguments)}");

		try {
			Directory.CreateDirectory(logDirectory);

			JsonRpcConnection connection = new(startInfo);
			connection.Log += message => Log?.Invoke(message);
			connection.Notification += OnNotification;

			connection.RequestHandler = (method, parameters) => method switch {
				"workspace/configuration" => LanguageServerOptions.Resolve(parameters, Settings),
				_ => null,
			};

			_connection = connection;

			Initialize(connection, solutionPath, projectPaths);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException
			or System.ComponentModel.Win32Exception) {
			SetState(LanguageServerState.Failed, "Failed to start");
			Log?.Invoke($"Language server failed to start: {exception.Message}");
		}
	}

	private void Initialize(JsonRpcConnection connection, string? solutionPath,
		IReadOnlyList<string> projectPaths) {
		JsonObject capabilities = new() {
			["textDocument"] = new JsonObject {
				["synchronization"] = new JsonObject {
					["dynamicRegistration"] = false,
					["didSave"] = true,
				},
				["publishDiagnostics"] = new JsonObject {
					["relatedInformation"] = false,
				},
				["completion"] = new JsonObject {
					["completionItem"] = new JsonObject {
						["snippetSupport"] = false,

						["labelDetailsSupport"] = true,
						["resolveSupport"] = new JsonObject {
							["properties"] = new JsonArray("documentation", "detail", "additionalTextEdits"),
						},
					},
				},
				["hover"] = new JsonObject {
					["contentFormat"] = new JsonArray("plaintext", "markdown"),
				},
				["definition"] = new JsonObject { ["dynamicRegistration"] = false },
				["implementation"] = new JsonObject { ["dynamicRegistration"] = false },
				["typeDefinition"] = new JsonObject { ["dynamicRegistration"] = false },
				["references"] = new JsonObject { ["dynamicRegistration"] = false },
				["formatting"] = new JsonObject { ["dynamicRegistration"] = false },
				["rangeFormatting"] = new JsonObject { ["dynamicRegistration"] = false },
				["rename"] = new JsonObject { ["dynamicRegistration"] = false },

				["documentSymbol"] = new JsonObject {
					["dynamicRegistration"] = false,
					["hierarchicalDocumentSymbolSupport"] = true,
				},
				["codeAction"] = new JsonObject {
					["dynamicRegistration"] = false,
					["codeActionLiteralSupport"] = new JsonObject {
						["codeActionKind"] = new JsonObject {
							["valueSet"] = new JsonArray("quickfix", "refactor", "source",
								"source.organizeImports"),
						},
					},
				},
				["signatureHelp"] = new JsonObject { ["dynamicRegistration"] = false },

				["semanticTokens"] = new JsonObject {
					["dynamicRegistration"] = false,
					["requests"] = new JsonObject { ["full"] = true },
					["tokenTypes"] = new JsonArray(),
					["tokenModifiers"] = new JsonArray(),
					["formats"] = new JsonArray("relative"),
				},

				["diagnostic"] = new JsonObject {
					["dynamicRegistration"] = false,
					["relatedDocumentSupport"] = false,
				},
			},
			["workspace"] = new JsonObject {
				["configuration"] = true,
				["workspaceFolders"] = true,
			},
		};

		JsonObject parameters = new() {
			["processId"] = Environment.ProcessId,
			["clientInfo"] = new JsonObject { ["name"] = "Forge", ["version"] = "0.1" },
			["capabilities"] = capabilities,
			["rootUri"] = null,

			["initializationOptions"] = new JsonObject {
				["completionInsertsUnimportedNamespaces"] = true,
			},
		};

		Task.Run(async () => {
			try {
				Task<JsonNode?> handshake = connection.SendRequestAsync("initialize", parameters);

				if (await Task.WhenAny(handshake, Task.Delay(TimeSpan.FromSeconds(60)))
					.ConfigureAwait(false) != handshake) {
					SetState(LanguageServerState.Failed, "Initialisation timed out");
					Log?.Invoke("The language server did not answer initialize within 60 seconds.");
					return;
				}

				JsonNode? result = await handshake.ConfigureAwait(false);

				if (result?["capabilities"] is JsonObject served) {
					Log?.Invoke($"Server capabilities: {string.Join(", ", served.Select(pair => pair.Key))}");

					if (served["semanticTokensProvider"]?["legend"]?["tokenTypes"] is JsonArray types) {
						_tokenTypes = [.. types.Select(entry => entry?.GetValue<string>() ?? string.Empty)];
					}
				}

				connection.SendNotification("initialized", new JsonObject());

				SetState(LanguageServerState.Running, "Loading projects...");

				if (solutionPath is { Length: > 0 }) {
					Log?.Invoke($"Opening solution {solutionPath}");
					connection.SendNotification("solution/open",
						new JsonObject { ["solution"] = solutionPath });
				}
				else if (projectPaths.Count > 0) {
					Log?.Invoke($"Opening {projectPaths.Count} project(s)");

					JsonArray projects = [];

					foreach (string project in projectPaths) projects.Add(project);

					connection.SendNotification("project/open", new JsonObject { ["projects"] = projects });
				}
				else SetState(LanguageServerState.Running, "Ready");
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException) {
				SetState(LanguageServerState.Failed, "Initialisation failed");
				Log?.Invoke($"Language server initialisation failed: {exception.Message}");
			}
		});
	}

	private void OnNotification(string method, JsonNode? parameters) {
		switch (method) {
			case "textDocument/publishDiagnostics":
				PublishDiagnostics(parameters);
				break;

			case "workspace/projectInitializationComplete":
				SetState(LanguageServerState.Running, "Ready");
				ProjectsLoaded?.Invoke();
				break;

			case "window/logMessage":
			case "window/showMessage":
				if (parameters?["message"]?.GetValue<string>() is { Length: > 0 } message) Log?.Invoke(message);

				break;
		}
	}

	private void PublishDiagnostics(JsonNode? parameters) {
		if (parameters?["uri"]?.GetValue<string>() is not { } uri ||
			parameters["diagnostics"] is not JsonArray diagnostics) return;

		string path = FromUri(uri);
		DiagnosticsPublished?.Invoke(path, ReadDiagnostics(path, diagnostics));
	}

	private static List<ServerDiagnostic> ReadDiagnostics(string path, JsonArray diagnostics) {
		List<ServerDiagnostic> results = [with(diagnostics.Count)];

		foreach (JsonNode? entry in diagnostics) {
			if (entry?["range"]?["start"] is not { } start) continue;

			int severity = entry["severity"]?.GetValue<int>() ?? 1;

			if (severity > 2) continue;

			string message = entry["message"]?.GetValue<string>() ?? string.Empty;
			string? code = entry["code"]?.ToString();

			results.Add(new ServerDiagnostic(
				path,
				(start["line"]?.GetValue<int>() ?? 0) + 1,
				(start["character"]?.GetValue<int>() ?? 0) + 1,
				code is { Length: > 0 } ? $"{code}: {message}" : message,
				severity == 1));
		}

		return results;
	}

	#region Documents

	public void DidOpen(string path, string text) {
		if (_connection is not { IsRunning: true } connection) return;

		lock (_sync) {
			_versions[path] = 1;
			_sent[path] = text;
		}

		connection.SendNotification("textDocument/didOpen", new JsonObject {
			["textDocument"] = new JsonObject {
				["uri"] = ToUri(path),
				["languageId"] = "csharp",
				["version"] = 1,
				["text"] = text,
			},
		});
	}

	public void DidChange(string path, string text) {
		if (_connection is not { IsRunning: true } connection) return;

		int version;
		string previous;

		lock (_sync) {
			version = _versions.TryGetValue(path, out int current) ? current + 1 : 1;
			_versions[path] = version;

			if (!_sent.TryGetValue(path, out previous!)) return;

			_sent[path] = text;
		}

		if (previous == text) return;

		int prefix = 0;
		int shortest = Math.Min(previous.Length, text.Length);

		while (prefix < shortest && previous[prefix] == text[prefix]) prefix++;

		int suffix = 0;

		while (suffix < shortest - prefix &&
			previous[^(suffix + 1)] == text[^(suffix + 1)]) suffix++;

		(int startLine, int startCharacter) = PositionOf(previous, prefix);
		(int endLine, int endCharacter) = PositionOf(previous, previous.Length - suffix);

		connection.SendNotification("textDocument/didChange", new JsonObject {
			["textDocument"] = new JsonObject {
				["uri"] = ToUri(path),
				["version"] = version,
			},
			["contentChanges"] = new JsonArray(new JsonObject {
				["range"] = new JsonObject {
					["start"] = new JsonObject {
						["line"] = startLine,
						["character"] = startCharacter,
					},
					["end"] = new JsonObject {
						["line"] = endLine,
						["character"] = endCharacter,
					},
				},
				["text"] = text[prefix..(text.Length - suffix)],
			}),
		});
	}

	private static (int Line, int Character) PositionOf(string text, int offset) {
		int line = 0;
		int lineStart = 0;

		for (int i = 0; i < offset && i < text.Length; i++) {
			if (text[i] == '\n') {
				line++;
				lineStart = i + 1;
			}
		}

		return (line, offset - lineStart);
	}

	private readonly Dictionary<string, string> _sent = [with(StringComparer.OrdinalIgnoreCase)];

	public void DidClose(string path) {
		if (_connection is not { IsRunning: true } connection) return;

		lock (_sync) {
			_versions.Remove(path);
			_sent.Remove(path);
		}

		connection.SendNotification("textDocument/didClose", new JsonObject {
			["textDocument"] = new JsonObject { ["uri"] = ToUri(path) },
		});
	}

	public void DidSave(string path) {
		if (_connection is { IsRunning: true } connection) {
			connection.SendNotification("textDocument/didSave", new JsonObject {
				["textDocument"] = new JsonObject { ["uri"] = ToUri(path) },
			});
		}
	}

	#endregion

	public async Task<string?> HoverAsync(string path, int line, int column) {
		if (_connection is not { IsRunning: true } connection) return null;

		try {
			JsonNode? result = await connection.SendRequestAsync("textDocument/hover", new JsonObject {
				["textDocument"] = DocumentIdentifier(path),
				["position"] = new JsonObject {
					["line"] = Math.Max(0, line - 1),
					["character"] = Math.Max(0, column - 1),
				},
			}).ConfigureAwait(false);

			return result?["contents"] switch {
				JsonObject markup => markup["value"]?.GetValue<string>(),
				JsonValue value => value.GetValue<string>(),
				JsonArray array when array.Count > 0 => array[0]?.ToString(),
				_ => null,
			};
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return null;
		}
	}

	public async Task<(string Path, int Line, int Column)?> DefinitionAsync(string path, int line, int column) {
		if (_connection is not { IsRunning: true } connection) return null;

		try {
			JsonNode? result = await connection.SendRequestAsync("textDocument/definition", new JsonObject {
				["textDocument"] = DocumentIdentifier(path),
				["position"] = new JsonObject {
					["line"] = Math.Max(0, line - 1),
					["character"] = Math.Max(0, column - 1),
				},
			}).ConfigureAwait(false);

			JsonNode? location = result is JsonArray array ? array.FirstOrDefault() : result;

			if (location?["uri"]?.GetValue<string>() is not { } uri ||
				location["range"]?["start"] is not { } start) return null;

			return (FromUri(uri),
				(start["line"]?.GetValue<int>() ?? 0) + 1,
				(start["character"]?.GetValue<int>() ?? 0) + 1);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return null;
		}
	}

	private string[] _tokenTypes = [];

	public async Task<IReadOnlyList<ServerToken>> SemanticTokensAsync(string path) {
		if (_connection is not { IsRunning: true } connection || _tokenTypes.Length == 0) return [];

		try {
			JsonNode? result = await connection.SendRequestAsync("textDocument/semanticTokens/full",
				new JsonObject {
					["textDocument"] = DocumentIdentifier(path),
				}).ConfigureAwait(false);

			if (result?["data"] is not JsonArray data || data.Count < 5) return [];

			List<ServerToken> tokens = [with(data.Count / 5)];

			int line = 0;
			int character = 0;

			for (int i = 0; i + 4 < data.Count; i += 5) {
				int lineDelta = data[i]?.GetValue<int>() ?? 0;
				int startDelta = data[i + 1]?.GetValue<int>() ?? 0;
				int length = data[i + 2]?.GetValue<int>() ?? 0;
				int type = data[i + 3]?.GetValue<int>() ?? 0;

				line += lineDelta;

				character = lineDelta == 0 ? character + startDelta : startDelta;

				if (length > 0 && type >= 0 && type < _tokenTypes.Length) {
					tokens.Add(new ServerToken(line + 1, character, length, _tokenTypes[type]));
				}
			}

			return tokens;
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return [];
		}
	}

	public async Task RequestDiagnosticsAsync(string path) {
		if (_connection is not { IsRunning: true } connection) return;

		try {
			JsonNode? result = await connection.SendRequestAsync("textDocument/diagnostic",
				new JsonObject {
					["textDocument"] = DocumentIdentifier(path),
				}).ConfigureAwait(false);

			if (result?["kind"]?.GetValue<string>() == "unchanged") return;

			if (result?["items"] is not JsonArray items) return;

			DiagnosticsPublished?.Invoke(path, ReadDiagnostics(path, items));
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
		}
	}

	public async Task<ProjectContexts?> ProjectContextsAsync(string path) {
		if (_connection is not { IsRunning: true } connection) return null;

		try {
			JsonNode? result = await connection.SendRequestAsync(
				"textDocument/_vs_getProjectContexts", new JsonObject {
					["_vs_textDocument"] = new JsonObject { ["uri"] = ToUri(path) },
				}).ConfigureAwait(false);

			if (result?["_vs_projectContexts"] is not JsonArray array || array.Count == 0) return null;

			List<ProjectContext> contexts = [];

			foreach (JsonNode? entry in array) {
				if (entry?["_vs_id"]?.GetValue<string>() is not { } id) continue;

				contexts.Add(new ProjectContext(id,
					entry["_vs_label"]?.GetValue<string>() ?? id,
					entry["_vs_is_miscellaneous"]?.GetValue<bool>() ?? false));
			}

			if (contexts.Count == 0) return null;

			int defaultIndex = Math.Clamp(result["_vs_defaultIndex"]?.GetValue<int>() ?? 0,
				0, contexts.Count - 1);

			ProjectContexts list = new(contexts, defaultIndex);

			lock (_sync) _contexts[path] = list;

			return list;
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return null;
		}
	}

	private readonly Dictionary<string, ProjectContexts> _contexts =
		[with(StringComparer.OrdinalIgnoreCase)];

	public void InvalidateContexts() {
		lock (_sync) _contexts.Clear();
	}

	public ProjectContexts? ContextsFor(string path) {
		lock (_sync) return _contexts.GetValueOrDefault(path);
	}

	public void SelectContext(string path, string id) {
		lock (_sync) {
			if (_contexts.TryGetValue(path, out ProjectContexts? list)) {
				_contexts[path] = list with { SelectedId = id };
			}
		}
	}

	private JsonObject DocumentIdentifier(string path) {
		JsonObject identifier = new() { ["uri"] = ToUri(path) };

		ProjectContexts? list;

		lock (_sync) _contexts.TryGetValue(path, out list);

		if (list?.Current is { IsMiscellaneous: false } current) {
			identifier["_vs_projectContext"] = new JsonObject {
				["_vs_id"] = current.Id,
				["_vs_label"] = current.Label,
			};
		}

		return identifier;
	}

	public Task<IReadOnlyList<ServerLocation>> ReferencesAsync(string path, int line, int column,
		bool includeDeclaration = true) =>
		LocationsAsync("textDocument/references", path, line, column, new JsonObject {
			["includeDeclaration"] = includeDeclaration,
		});

	public Task<IReadOnlyList<ServerLocation>> ImplementationsAsync(string path, int line, int column) =>
		LocationsAsync("textDocument/implementation", path, line, column);

	public Task<IReadOnlyList<ServerLocation>> DefinitionsAsync(string path, int line, int column) =>
		LocationsAsync("textDocument/definition", path, line, column);

	private async Task<IReadOnlyList<ServerLocation>> LocationsAsync(string method, string path,
		int line, int column, JsonObject? context = null) {
		if (_connection is not { IsRunning: true } connection) return [];

		try {
			JsonObject parameters = new() {
				["textDocument"] = DocumentIdentifier(path),
				["position"] = new JsonObject {
					["line"] = Math.Max(0, line - 1),
					["character"] = Math.Max(0, column - 1),
				},
			};

			if (context is not null) parameters["context"] = context;

			JsonNode? result = await connection.SendRequestAsync(method, parameters).ConfigureAwait(false);

			List<ServerLocation> locations = [];

			foreach (JsonNode? entry in result as JsonArray ?? (result is null ? [] : [result])) {
				string? uri = entry?["uri"]?.GetValue<string>() ??
					entry?["targetUri"]?.GetValue<string>();

				JsonNode? range = entry?["range"] ?? entry?["targetSelectionRange"] ??
					entry?["targetRange"];

				if (uri is null || range?["start"] is not { } start) continue;

				string file = FromUri(uri);
				int targetLine = (start["line"]?.GetValue<int>() ?? 0) + 1;

				locations.Add(new ServerLocation(file, targetLine,
					(start["character"]?.GetValue<int>() ?? 0) + 1, ReadLine(file, targetLine)));
			}

			return locations;
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return [];
		}
	}

	private static string ReadLine(string path, int line) {
		try {
			int number = 1;

			foreach (string text in File.ReadLines(path)) {
				if (number++ == line) return text.Trim();
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}

		return string.Empty;
	}

	public async Task<IReadOnlyList<ServerSymbol>> DocumentSymbolsAsync(string path) {
		if (_connection is not { IsRunning: true } connection) return [];

		try {
			JsonNode? result = await connection.SendRequestAsync("textDocument/documentSymbol",
				new JsonObject {
					["textDocument"] = DocumentIdentifier(path),
				}).ConfigureAwait(false);

			List<ServerSymbol> symbols = [];
			Collect(result as JsonArray, 0, symbols);
			return symbols;
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return [];
		}

		static void Collect(JsonArray? array, int depth, List<ServerSymbol> into) {
			foreach (JsonNode? entry in array ?? []) {
				if (entry?["name"]?.GetValue<string>() is not { } name) continue;

				JsonNode? range = entry["selectionRange"] ?? entry["range"] ??
					entry["location"]?["range"];

				into.Add(new ServerSymbol(name, entry["kind"]?.GetValue<int>() ?? 0, depth,
					(range?["start"]?["line"]?.GetValue<int>() ?? 0) + 1,
					(range?["start"]?["character"]?.GetValue<int>() ?? 0) + 1));

				Collect(entry["children"] as JsonArray, depth + 1, into);
			}
		}
	}

	public async Task<IReadOnlyList<ServerTextEdit>> FormatAsync(string path, int tabSize,
		bool insertSpaces) {
		if (_connection is not { IsRunning: true } connection) return [];

		try {
			JsonNode? result = await connection.SendRequestAsync("textDocument/formatting",
				new JsonObject {
					["textDocument"] = DocumentIdentifier(path),
					["options"] = new JsonObject {
						["tabSize"] = tabSize,
						["insertSpaces"] = insertSpaces,
					},
				}).ConfigureAwait(false);

			List<ServerTextEdit> edits = [];

			foreach (JsonNode? entry in result as JsonArray ?? []) {
				if (entry?["range"]?["start"] is not { } start ||
					entry["range"]?["end"] is not { } end) continue;

				edits.Add(new ServerTextEdit(
					(start["line"]?.GetValue<int>() ?? 0) + 1,
					(start["character"]?.GetValue<int>() ?? 0) + 1,
					(end["line"]?.GetValue<int>() ?? 0) + 1,
					(end["character"]?.GetValue<int>() ?? 0) + 1,
					entry["newText"]?.GetValue<string>() ?? string.Empty));
			}

			edits.Sort(static (left, right) => right.StartLine != left.StartLine
				? right.StartLine.CompareTo(left.StartLine)
				: right.StartColumn.CompareTo(left.StartColumn));

			return edits;
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return [];
		}
	}

	#region Completion

	private JsonNode?[] _completionItems = [];

	private JsonObject? _completionDefaults;

	public async Task<ServerCompletionList> CompletionAsync(string path, int line,
		int column, char trigger, int triggerKind) {
		if (_connection is not { IsRunning: true } connection) return new ServerCompletionList([], false);

		try {
			JsonObject context = trigger == '\0'
				? new JsonObject { ["triggerKind"] = 1 }
				: new JsonObject {
					["triggerKind"] = 2,
					["triggerCharacter"] = trigger.ToString(),
				};

			JsonNode? result = await connection.SendRequestAsync("textDocument/completion",
				new JsonObject {
					["textDocument"] = DocumentIdentifier(path),
					["position"] = new JsonObject {
						["line"] = Math.Max(0, line - 1),
						["character"] = Math.Max(0, column - 1),
					},
					["context"] = context,
				}).ConfigureAwait(false);

			JsonArray items = (result is JsonArray array ? array : result?["items"] as JsonArray) ?? [];

			_completionDefaults = result?["itemDefaults"] as JsonObject;
			_completionItems = [.. items];

			List<ServerCompletion> completions = [with(items.Count)];

			for (int i = 0; i < items.Count; i++) {
				if (items[i]?["label"]?.GetValue<string>() is not { Length: > 0 } label) continue;

				JsonNode? item = items[i];

				JsonNode? edit = item?["textEdit"];
				JsonNode? range = edit?["range"] ?? edit?["replace"] ??
					_completionDefaults?["editRange"]?["replace"] ??
					_completionDefaults?["editRange"];

				string insert = edit?["newText"]?.GetValue<string>() ??
					item?["insertText"]?.GetValue<string>() ?? label;

				JsonNode? labelDetails = item?["labelDetails"];

				bool hasRange = range?["start"]?["character"] is not null;

				completions.Add(new ServerCompletion(
					Index: i,
					Label: label,
					LabelDetail: labelDetails?["detail"]?.GetValue<string>() ?? string.Empty,
					LabelDescription: labelDetails?["description"]?.GetValue<string>() ?? string.Empty,
					InsertText: insert,
					Detail: item?["detail"]?.GetValue<string>() ?? string.Empty,
					Kind: KindName(item?["kind"]?.GetValue<int>() ?? 0),
					SortText: item?["sortText"]?.GetValue<string>() ?? label,
					FilterText: item?["filterText"]?.GetValue<string>() ?? label,
					Preselect: item?["preselect"]?.GetValue<bool>() ?? false,
					ReplaceLine: (range?["start"]?["line"]?.GetValue<int>() ?? (line - 1)) + 1,
					ReplaceStartColumn: hasRange ? range!["start"]!["character"]!.GetValue<int>() + 1 : 0,
					ReplaceEndColumn: hasRange
						? (range!["end"]?["character"]?.GetValue<int>() ?? (column - 1)) + 1
						: 0));
			}

			completions.Sort(static (left, right) => {
				int order = string.CompareOrdinal(left.SortText, right.SortText);
				return order != 0 ? order : string.CompareOrdinal(left.Label, right.Label);
			});

			return new ServerCompletionList(completions,
				result?["isIncomplete"]?.GetValue<bool>() ?? false);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return new ServerCompletionList([], false);
		}
	}

	public async Task<ServerCompletionDetail?> ResolveCompletionAsync(int index) {
		if (_connection is not { IsRunning: true } connection ||
			index < 0 || index >= _completionItems.Length ||
			_completionItems[index] is not { } item) return null;

		try {
			JsonObject request = item.DeepClone().AsObject();

			if (_completionDefaults?["data"] is { } sharedData && request["data"] is null) {
				request["data"] = sharedData.DeepClone();
			}

			JsonNode? result = await connection
				.SendRequestAsync("completionItem/resolve", request).ConfigureAwait(false);

			if (result is null) return null;

			string documentation = result["documentation"] switch {
				JsonObject markup => markup["value"]?.GetValue<string>() ?? string.Empty,
				JsonValue value => value.GetValue<string>(),
				_ => string.Empty,
			};

			List<ServerTextEdit> additional = [];

			foreach (JsonNode? entry in result["additionalTextEdits"] as JsonArray ?? []) {
				if (entry?["range"]?["start"] is not { } start || entry["range"]?["end"] is not { } end) continue;

				additional.Add(new ServerTextEdit(
					(start["line"]?.GetValue<int>() ?? 0) + 1,
					(start["character"]?.GetValue<int>() ?? 0) + 1,
					(end["line"]?.GetValue<int>() ?? 0) + 1,
					(end["character"]?.GetValue<int>() ?? 0) + 1,
					entry["newText"]?.GetValue<string>() ?? string.Empty));
			}

			return new ServerCompletionDetail(
				result["detail"]?.GetValue<string>() ?? string.Empty, documentation, additional);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return null;
		}
	}

	private static string KindName(int kind) => kind switch {
		1 => "Text",
		2 => "Method",
		3 => "Function",
		4 => "Constructor",
		5 => "Field",
		6 => "Variable",
		7 => "Class",
		8 => "Interface",
		9 => "Module",
		10 => "Property",
		11 => "Unit",
		12 => "Value",
		13 => "Enum",
		14 => "Keyword",
		15 => "Snippet",
		16 => "Color",
		17 => "File",
		18 => "Reference",
		19 => "Folder",
		20 => "EnumMember",
		21 => "Constant",
		22 => "Struct",
		23 => "Event",
		24 => "Operator",
		25 => "TypeParameter",
		_ => "Text",
	};

	public async Task<ServerSignatures?> SignatureHelpAsync(string path, int line, int column,
		char trigger) {
		if (_connection is not { IsRunning: true } connection) return null;

		try {
			JsonNode? result = await connection.SendRequestAsync("textDocument/signatureHelp",
				new JsonObject {
					["textDocument"] = DocumentIdentifier(path),
					["position"] = new JsonObject {
						["line"] = Math.Max(0, line - 1),
						["character"] = Math.Max(0, column - 1),
					},
					["context"] = trigger == '\0'
						? new JsonObject { ["triggerKind"] = 1, ["isRetrigger"] = false }
						: new JsonObject {
							["triggerKind"] = 2,
							["triggerCharacter"] = trigger.ToString(),
							["isRetrigger"] = false,
						},
				}).ConfigureAwait(false);

			if (result?["signatures"] is not JsonArray signatures || signatures.Count == 0) return null;

			int active = result["activeSignature"]?.GetValue<int>() ?? 0;
			int activeParameter = result["activeParameter"]?.GetValue<int>() ?? 0;

			List<ServerSignature> overloads = [with(signatures.Count)];

			foreach (JsonNode? entry in signatures) {
				if (entry?["label"]?.GetValue<string>() is not { } label) continue;

				List<(int Start, int Length)> parameters = [];

				foreach (JsonNode? parameter in entry["parameters"] as JsonArray ?? []) {
					if (parameter?["label"] is JsonArray offsets && offsets.Count == 2) {
						int start = offsets[0]?.GetValue<int>() ?? 0;
						parameters.Add((start, (offsets[1]?.GetValue<int>() ?? start) - start));
					}
					else if (parameter?["label"]?.GetValue<string>() is { Length: > 0 } text &&
						label.IndexOf(text, StringComparison.Ordinal) is >= 0 and int at) parameters.Add((at, text.Length));
					else parameters.Add((0, 0));
				}

				string documentation = entry["documentation"] switch {
					JsonObject markup => markup["value"]?.GetValue<string>() ?? string.Empty,
					JsonValue value => value.GetValue<string>(),
					_ => string.Empty,
				};

				overloads.Add(new ServerSignature(label, parameters, documentation,
					entry["activeParameter"]?.GetValue<int>() ?? -1));
			}

			return overloads.Count == 0
				? null
				: new ServerSignatures(overloads, Math.Clamp(active, 0, overloads.Count - 1),
					Math.Max(0, activeParameter));
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException) {
			return null;
		}
	}

	#endregion

	public void Stop() {
		_connection?.Dispose();
		_connection = null;

		lock (_sync) {
			_versions.Clear();
			_sent.Clear();
		}

		SetState(LanguageServerState.Stopped, "Stopped");
	}

	private void SetState(LanguageServerState state, string text) {
		State = state;
		StatusText = text;
		StateChanged?.Invoke();
	}

	public static string ToUri(string path) =>
		"file:///" + path.Replace('\\', '/').Replace(" ", "%20", StringComparison.Ordinal);

	public static string FromUri(string uri) {
		string path = uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? uri[8..] : uri;

		return Uri.UnescapeDataString(path).Replace('/', '\\');
	}

	public void Dispose() => Stop();
	[GeneratedRegex(@"--[A-Za-z][A-Za-z0-9-]*")]
	private static partial Regex MyRegex();
}
