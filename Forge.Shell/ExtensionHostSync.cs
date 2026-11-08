using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

using Forge.Common.Commands;
using Forge.Core.Native;
using Forge.Editor;
using Forge.Terminal;
using Forge.Text;
using Forge.VSCode;
using Forge.Workbench;
using Forge.Workspace;

namespace Forge.Shell;

public sealed class ExtensionHostSync
{
	private readonly Workbench _workbench;
	private readonly VSCodeBridge _bridge;
	private readonly Action<Action> _post;
	private readonly Action _refresh;

	private readonly Dictionary<Document, Action<TextBuffer, TextChange>> _tracked = [];
	private readonly Dictionary<string, JsonObject> _virtualUris = new(StringComparer.Ordinal);
	private readonly Dictionary<string, TerminalSession> _terminals = new(StringComparer.Ordinal);
	private readonly ConditionalWeakTable<object, string> _tabIds = [];

	private int _nextTabId;
	private int _stateSignature;
	private bool _applyingRemote;

	public ExtensionHostSync(Workbench workbench, VSCodeBridge bridge, Action<Action> post, Action refresh) {
		_workbench = workbench;
		_bridge = bridge;
		_post = post;
		_refresh = refresh;

		workbench.DocumentAdded += Track;
		workbench.DocumentRemoved += Untrack;
		workbench.DocumentSaving += WillSave;
		workbench.DocumentSaved += Saved;

		workbench.Panel.ProblemsChanged += PushDiagnostics;

		bridge.WorkbenchRequest = OnRequest;
		bridge.WorkbenchNotification = (method, parameters) => _post(() => OnNotification(method, parameters));
	}

	public void Start() {
		foreach (Document document in _workbench.Documents) {
			if (_workbench.IsPaneAt(_workbench.IndexOf(document))) continue;

			Track(document);
		}

		_stateSignature = 0;
		Tick();
		PushDiagnostics();
	}

	private void PushDiagnostics() => _bridge.Notify("languages/setDiagnostics", new JsonObject { ["entries"] = Diagnostics(null) });

	#region Documents

	private JsonObject? UriOf(Document document) {
		if (document.Path is { } path) return HostEditor.UriFor(path);

		return document.VirtualUri is { } key && _virtualUris.TryGetValue(key, out JsonObject? uri) ? (JsonObject)uri.DeepClone() : null;
	}

	private static string LanguageOf(Document document) {
		string extension = Path.GetExtension(document.Path ?? document.Name);

		return extension.Length > 1 ? extension[1..].ToLowerInvariant() switch {
			"cs" or "csx" => "csharp",
			"ts" or "mts" or "cts" => "typescript",
			"tsx" => "typescriptreact",
			"js" or "mjs" or "cjs" => "javascript",
			"jsx" => "javascriptreact",
			"md" => "markdown",
			"py" => "python",
			"ps1" => "powershell",
			"csproj" or "props" or "targets" or "slnx" => "xml",
			"yml" => "yaml",
			string other => other,
		} : "plaintext";
	}

	private void Track(Document document) {
		if (_tracked.ContainsKey(document) || UriOf(document) is not { } uri) return;

		void Changed(TextBuffer buffer, TextChange change) {
			if (_applyingRemote && document.VirtualUri is not null) return;

			if (UriOf(document) is not { } current) return;

			_bridge.Notify("documents/changed", new JsonObject {
				["uri"] = current,
				["version"] = buffer.Version,
				["isDirty"] = document.IsDirty(),
				["changes"] = new JsonArray {
					new JsonObject {
						["offset"] = change.Offset,
						["length"] = change.RemovedLength,
						["text"] = buffer.InsertedText(change).ToString(),
					},
				},
			});
		}

		_tracked[document] = Changed;
		document.Buffer.Changed += Changed;

		_bridge.Notify("documents/opened", new JsonObject {
			["uri"] = uri,
			["text"] = document.Buffer.GetText(),
			["languageId"] = LanguageOf(document),
			["version"] = document.Buffer.Version,
			["isDirty"] = document.IsDirty(),
		});
	}

	private void Untrack(Document document) {
		if (!_tracked.Remove(document, out Action<TextBuffer, TextChange>? handler)) return;

		document.Buffer.Changed -= handler;

		if (UriOf(document) is { } uri) _bridge.Notify("documents/closed", new JsonObject { ["uri"] = uri });
	}

	private void WillSave(Document document) {
		if (document.Path is null || UriOf(document) is not { } uri) return;

		_bridge.Notify("documents/willSave", new JsonObject { ["uri"] = uri, ["reason"] = 1 });
	}

	private void Saved(Document document) {
		if (document.Path is null || UriOf(document) is not { } uri) return;

		_bridge.Notify("documents/saved", new JsonObject { ["uri"] = uri });
	}

	private Document? FindDocument(JsonNode? uri) {
		if (uri is null) return null;

		if (Scheme(uri) == "file") {
			string path = FsPath(uri);

			foreach (Document document in _workbench.Documents) {
				if (string.Equals(document.Path, path, StringComparison.OrdinalIgnoreCase)) return document;
			}

			return null;
		}

		return _workbench.FindVirtual(External(uri));
	}

	private static string Scheme(JsonNode uri) => uri["scheme"]?.GetValue<string>() ?? "file";

	private static string External(JsonNode uri) => uri["external"]?.GetValue<string>() ?? $"{Scheme(uri)}:{uri["path"]?.GetValue<string>()}";

	private static string FsPath(JsonNode uri) {
		if (uri["fsPath"]?.GetValue<string>() is { Length: > 0 } fsPath) return fsPath;

		string path = uri["path"]?.GetValue<string>() ?? string.Empty;

		if (path.Length > 2 && path[0] == '/' && path[2] == ':') path = path[1..];

		return path.Replace('/', '\\');
	}

	private static string DisplayName(JsonNode uri) {
		string path = uri["path"]?.GetValue<string>() ?? string.Empty;
		int slash = path.LastIndexOf('/');
		return slash >= 0 ? path[(slash + 1)..] : path;
	}

	private Document OpenVirtual(JsonNode uri, string text, string name) {
		string key = External(uri);
		_virtualUris[key] = (JsonObject)uri.DeepClone();

		if (_workbench.FindVirtual(key) is { } existing) {
			_workbench.OpenVirtual(existing);
			return existing;
		}

		Document document = Document.Virtual(key, name, text);
		document.SaveHandler = content => {
			_ = _bridge.RequestAsync("documents/write", new JsonObject { ["uri"] = uri.DeepClone(), ["text"] = content });
			return true;
		};

		_workbench.OpenVirtual(document);
		return document;
	}

	private void ReplaceText(Document document, string text) {
		string current = document.Buffer.GetText();

		if (string.Equals(current, text, StringComparison.Ordinal)) return;

		_applyingRemote = true;

		try {
			document.Buffer.ReplaceChanged(current, text);
		}
		finally {
			_applyingRemote = false;
		}
	}

	#endregion

	#region State

	public void Tick() {
		TextView? view = _workbench.ActiveView;
		HashCode hash = new();
		hash.Add(_workbench.ActiveIndex);
		hash.Add(_workbench.Documents.Count);

		if (view is not null) {
			hash.Add(view.CaretOffset);
			hash.Add(view.SelectionAnchor);
			hash.Add((int)view.ScrollY);
		}

		foreach (Document document in _workbench.Documents) hash.Add(document.IsDirty());

		int signature = hash.ToHashCode();

		if (signature == _stateSignature) return;

		_stateSignature = signature;
		_bridge.Notify("host/state", BuildState());
	}

	private JsonObject BuildState() {
		JsonArray tabs = [];
		JsonArray visible = [];
		JsonObject? active = null;

		for (int i = 0; i < _workbench.Documents.Count; i++) {
			Document document = _workbench.Documents[i];
			bool isActive = i == _workbench.ActiveIndex;
			object owner = _workbench.FindPaneAt(i) is { } pane ? pane : document;
			JsonObject tab = new() {
				["id"] = TabId(owner),
				["label"] = _workbench.FindPaneAt(i)?.Title ?? document.Name,
				["isActive"] = isActive,
				["isDirty"] = document.IsDirty(),
				["isPinned"] = false,
				["isPreview"] = false,
			};

			if (_workbench.FindPaneAt(i) is null && UriOf(document) is { } uri) {
				tab["input"] = document.DiffOriginalUri is { } original && _virtualUris.TryGetValue(original, out JsonObject? originalUri)
					? new JsonObject { ["kind"] = "diff", ["original"] = originalUri.DeepClone(), ["modified"] = uri.DeepClone() }
					: new JsonObject { ["kind"] = "text", ["uri"] = uri.DeepClone() };

				if (isActive) {
					active = EditorState(document, _workbench.ViewAt(i), uri);
					visible.Add(active.DeepClone());
				}
			}

			tabs.Add(tab);
		}

		return new JsonObject {
			["activeTextEditor"] = active,
			["visibleTextEditors"] = visible,
			["tabGroups"] = new JsonArray { new JsonObject { ["viewColumn"] = 1, ["isActive"] = true, ["tabs"] = tabs } },
			["shell"] = Environment.GetEnvironmentVariable("ComSpec"),
		};
	}

	private string TabId(object owner) => _tabIds.GetValue(owner, _ => $"tab-{++_nextTabId}");

	private static JsonObject Point(TextSnapshot snapshot, int offset) {
		int line = snapshot.GetLineAt(Math.Clamp(offset, 0, snapshot.Length));
		return new JsonObject { ["line"] = line, ["character"] = offset - snapshot.GetLineSpan(line).Start };
	}

	private static JsonObject EditorState(Document document, TextView view, JsonObject uri) {
		TextSnapshot snapshot = view.Snapshot;
		int first = Math.Clamp(view.FirstVisibleLine, 0, Math.Max(0, snapshot.LineCount - 1));
		int last = Math.Clamp(view.LastVisibleLine, first, Math.Max(0, snapshot.LineCount - 1));

		return new JsonObject {
			["document"] = new JsonObject { ["uri"] = uri.DeepClone(), ["languageId"] = LanguageOf(document) },
			["selections"] = new JsonArray { new JsonObject { ["anchor"] = Point(snapshot, view.SelectionAnchor), ["active"] = Point(snapshot, view.CaretOffset) } },
			["visibleRanges"] = new JsonArray { new JsonObject { ["start"] = new JsonObject { ["line"] = first, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = last, ["character"] = 0 } } },
			["viewColumn"] = 1,
			["tabSize"] = view.TabWidth,
		};
	}

	#endregion

	#region Requests

	private Task<JsonNode?> OnUi(Func<JsonNode?> action) {
		TaskCompletionSource<JsonNode?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		_post(() => {
			try {
				completion.TrySetResult(action());
			}
			catch (Exception exception) {
				_bridge.Log($"Extension request failed: {exception.Message}");
				completion.TrySetResult(null);
			}

			_refresh();
		});

		return completion.Task;
	}

	private Task<JsonNode?>? OnRequest(string method, JsonNode? parameters) => method switch {
		"documents/open" => OnUi(() => OpenDocument(parameters)),
		"documents/save" => OnUi(() => FindDocument(parameters?["uri"]) is { } document && _workbench.Save(document)),
		"window/showTextDocument" => OnUi(() => ShowTextDocument(parameters)),
		"window/openDiff" => OnUi(() => OpenDiff(parameters)),
		"window/closeTabs" => OnUi(() => CloseTabs(parameters)),
		"workspace/applyEdit" => OnUi(() => ApplyEdit(parameters)),
		"languages/getDiagnostics" => OnUi(() => Diagnostics(parameters)),
		"env/clipboardRead" => OnUi(() => Clipboard.GetText()),
		"env/clipboardWrite" => OnUi(() => {
			Clipboard.SetText(parameters?["value"]?.GetValue<string>() ?? string.Empty);
			return true;
		}),
		"env/openExternal" => Task.FromResult<JsonNode?>(OpenExternal(parameters?["target"]?.GetValue<string>())),
		"terminal/processId" => Task.FromResult<JsonNode?>(null),
		"workspace/updateConfiguration" => Task.FromResult<JsonNode?>(UpdateConfiguration(parameters)),
		"window/showInputBox" => Task.FromResult<JsonNode?>(null),
		_ => null,
	};

	private JsonNode? UpdateConfiguration(JsonNode? parameters) {
		if (parameters?["key"]?.GetValue<string>() is not { Length: > 0 } key) return false;

		JsonNode? value = parameters["value"]?.DeepClone();
		_post(() => Forge.Core.Settings.Current.SetValue(key, value));
		return true;
	}

	private static JsonNode? OpenExternal(string? target) {
		if (string.IsNullOrWhiteSpace(target)) return false;

		try {
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
			return true;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) {
			return false;
		}
	}

	private JsonNode? OpenDocument(JsonNode? parameters) {
		if (parameters?["uri"] is not { } uri) return null;

		if (FindDocument(uri) is { } open) {
			return new JsonObject { ["text"] = open.Buffer.GetText(), ["languageId"] = LanguageOf(open), ["version"] = open.Buffer.Version, ["isDirty"] = open.IsDirty() };
		}

		if (Scheme(uri) != "file") return null;

		string path = FsPath(uri);

		if (!File.Exists(path)) return null;

		return new JsonObject { ["text"] = File.ReadAllText(path), ["languageId"] = Path.GetExtension(path).TrimStart('.'), ["version"] = 1, ["isDirty"] = false };
	}

	private JsonNode? ShowTextDocument(JsonNode? parameters) {
		if (parameters?["uri"] is not { } uri) return null;

		Document? document;

		if (Scheme(uri) == "file") {
			string path = FsPath(uri);

			if (!File.Exists(path)) return null;

			_workbench.OpenFile(path);
			document = FindDocument(uri);
		}
		else {
			document = OpenVirtual(uri, parameters["text"]?.GetValue<string>() ?? string.Empty, DisplayName(uri));
		}

		if (document is null || parameters["options"]?["selection"] is not JsonObject selection) return true;

		int index = _workbench.IndexOf(document);

		if (index < 0) return true;

		TextView view = _workbench.ViewAt(index);
		view.MoveTo(OffsetOf(view.Snapshot, selection["start"]), extend: false);
		view.MoveTo(OffsetOf(view.Snapshot, selection["end"]), extend: true);
		return true;
	}

	private static int OffsetOf(TextSnapshot snapshot, JsonNode? point) {
		int line = Math.Clamp(point?["line"]?.GetValue<int>() ?? 0, 0, Math.Max(0, snapshot.LineCount - 1));
		TextSpan span = snapshot.GetLineSpan(line);
		return span.Start + Math.Clamp(point?["character"]?.GetValue<int>() ?? 0, 0, span.Length);
	}

	private JsonNode? OpenDiff(JsonNode? parameters) {
		if (parameters?["modified"] is not { } modified || parameters["original"] is not { } original) return null;

		string originalKey = External(original);
		_virtualUris[originalKey] = (JsonObject)original.DeepClone();

		float previousLineHeight = _workbench.ActiveView?.LastLineHeight ?? 0f;
		float previousViewport = _workbench.ActiveView?.LastViewportHeight ?? 0f;
		string originalText = parameters["originalText"]?.GetValue<string>() ?? string.Empty;
		string title = parameters["title"]?.GetValue<string>() is { Length: > 0 } given ? given : DisplayName(modified);
		Document? document;

		if (Scheme(modified) == "file") {
			document = _workbench.OpenDiffTab(FsPath(modified), originalKey, title, preview: parameters["preview"]?.GetValue<bool>() ?? true);
		}
		else {
			document = OpenVirtual(modified, parameters["modifiedText"]?.GetValue<string>() ?? string.Empty, title);
		}

		if (document is null) return null;

		_workbench.ShowDiff(document, originalKey, originalText, previousLineHeight, previousViewport);
		_stateSignature = 0;
		return true;
	}

	private JsonNode? CloseTabs(JsonNode? parameters) {
		if (parameters?["tabs"] is not JsonArray ids) return false;

		HashSet<string> wanted = [with(StringComparer.Ordinal)];

		foreach (JsonNode? id in ids) {
			if (id?.GetValue<string>() is { } value) wanted.Add(value);
		}

		for (int i = _workbench.Documents.Count - 1; i >= 0; i--) {
			object owner = _workbench.FindPaneAt(i) is { } pane ? pane : _workbench.Documents[i];

			if (_tabIds.TryGetValue(owner, out string? id) && wanted.Contains(id)) _workbench.Close(i);
		}

		_stateSignature = 0;
		return true;
	}

	private JsonNode? ApplyEdit(JsonNode? parameters) {
		if (parameters?["entries"] is not JsonArray entries) return false;

		foreach (JsonNode? entry in entries) {
			if (entry?["uri"] is not { } uri || entry["edits"] is not JsonArray edits) continue;

			Document? document = FindDocument(uri);

			if (document is null && Scheme(uri) == "file") {
				_workbench.OpenFile(FsPath(uri));
				document = FindDocument(uri);
			}

			if (document is null) return false;

			TextSnapshot snapshot = document.Buffer.CreateSnapshot();
			List<(int Start, int End, string Text)> changes = [];

			foreach (JsonNode? edit in edits) {
				int start = OffsetOf(snapshot, edit?["range"]?["start"]);
				int end = OffsetOf(snapshot, edit?["range"]?["end"]);
				changes.Add((start, Math.Max(start, end), edit?["newText"]?.GetValue<string>() ?? string.Empty));
			}

			changes.Sort(static (left, right) => right.Start.CompareTo(left.Start));

			document.Buffer.BeginEdit();

			foreach ((int start, int end, string text) in changes) document.Buffer.Replace(start, end - start, text);

			document.Buffer.EndEdit();
		}

		_workbench.ActiveDocumentChanged?.Invoke();
		return true;
	}

	private JsonNode? Diagnostics(JsonNode? parameters) {
		string? only = parameters?["uri"] is { } uri ? FsPath(uri) : null;
		Dictionary<string, JsonArray> byFile = new(StringComparer.OrdinalIgnoreCase);

		foreach (Problem problem in _workbench.Panel.Problems) {
			if (string.IsNullOrEmpty(problem.File) || (only is not null && !string.Equals(problem.File, only, StringComparison.OrdinalIgnoreCase))) continue;

			if (!byFile.TryGetValue(problem.File, out JsonArray? list)) byFile[problem.File] = list = [];

			JsonObject at = new() { ["line"] = Math.Max(0, problem.Line - 1), ["character"] = Math.Max(0, problem.Column - 1) };
			list.Add(new JsonObject {
				["range"] = new JsonObject { ["start"] = at.DeepClone(), ["end"] = at },
				["message"] = problem.Message,
				["severity"] = problem.Severity switch { DiagnosticSeverity.Error => 0, DiagnosticSeverity.Warning => 1, _ => 2 },
				["source"] = problem.Build ? "build" : "csharp",
				["code"] = problem.Code,
			});
		}

		if (only is not null) return byFile.TryGetValue(only, out JsonArray? single) ? single : new JsonArray();

		JsonArray all = [];

		foreach ((string file, JsonArray list) in byFile) all.Add(new JsonArray { HostEditor.UriFor(file), list });

		return all;
	}

	#endregion

	#region Notifications

	private void OnNotification(string method, JsonNode? parameters) {
		switch (method) {
			case "documents/virtualOpened":
				if (parameters?["uri"] is { } opened) _virtualUris[External(opened)] = (JsonObject)opened.DeepClone();

				break;

			case "documents/virtualChanged":
				if (parameters?["uri"] is { } changed && FindDocument(changed) is { } target) ReplaceText(target, parameters["text"]?.GetValue<string>() ?? string.Empty);

				break;

			case "editor/setSelection":
				if (FindDocument(parameters?["uri"]) is { } selected && _workbench.IndexOf(selected) is >= 0 and int selectedIndex && parameters?["selections"] is JsonArray { Count: > 0 } selections) {
					TextView view = _workbench.ViewAt(selectedIndex);
					view.MoveTo(OffsetOf(view.Snapshot, selections[0]?["anchor"] ?? selections[0]?["start"]), extend: false);
					view.MoveTo(OffsetOf(view.Snapshot, selections[0]?["active"] ?? selections[0]?["end"]), extend: true);
				}

				break;

			case "editor/reveal":
				if (FindDocument(parameters?["uri"]) is { } revealed && _workbench.IndexOf(revealed) is >= 0 and int revealIndex) {
					TextView view = _workbench.ViewAt(revealIndex);
					view.MoveTo(OffsetOf(view.Snapshot, parameters?["range"]?["start"]), extend: false);
					_workbench.RevealCaret?.Invoke();
				}

				break;

			case "status/set":
				if (parameters?["id"]?.GetValue<string>() is { } statusId) {
					_workbench.StatusBar.SetItem(new StatusBar.Item(statusId, default, parameters["text"]?.GetValue<string>() ?? string.Empty, parameters["command"]?.GetValue<string>(), parameters["alignment"]?.GetValue<int>() == 2, parameters["priority"]?.GetValue<int?>() ?? 0));
				}

				break;

			case "status/remove":
				if (parameters?["id"]?.GetValue<string>() is { } removed) _workbench.StatusBar.RemoveItem(removed);

				break;

			case "terminal/create":
				CreateTerminal(parameters);
				break;

			case "terminal/sendText":
				if (TerminalFor(parameters) is { } input) input.Write((parameters?["text"]?.GetValue<string>() ?? string.Empty) + (parameters?["execute"]?.GetValue<bool>() == false ? string.Empty : "\r"));

				break;

			case "terminal/show":
				if (TerminalFor(parameters) is { } shown) _workbench.Panel.ShowTerminal(shown, parameters?["preserveFocus"]?.GetValue<bool>() != true);

				break;

			case "terminal/dispose":
				if (parameters?["id"]?.GetValue<string>() is { } disposed && _terminals.Remove(disposed, out TerminalSession? closing)) _workbench.Panel.CloseTerminal(closing);

				break;

			case "output/clear":
				if (parameters?["name"]?.GetValue<string>() is { } cleared) _workbench.Panel.Channel(cleared).Clear();

				break;

			case "documents/dirty" or "editor/setDecorations" or "editor/decorationType" or "editor/disposeDecorationType" or "output/show" or "webview/registerProvider" or "webview/unregisterProvider" or "commands/unregister" or "webview/show" or "host/ready" or "webview/dispose":
				break;

			default:
				_bridge.Log($"Unhandled API notification: {method}");
				break;
		}

		_refresh();
	}

	private TerminalSession? TerminalFor(JsonNode? parameters) =>
		parameters?["id"]?.GetValue<string>() is { } id && _terminals.TryGetValue(id, out TerminalSession? session) ? session : null;

	private void CreateTerminal(JsonNode? parameters) {
		if (parameters?["id"]?.GetValue<string>() is not { } id) return;

		string name = parameters["name"]?.GetValue<string>() ?? "Terminal";
		TerminalProfile profile;

		if (parameters["shellPath"]?.GetValue<string>() is { Length: > 0 } shell) {
			List<string> arguments = [];

			foreach (JsonNode? argument in parameters["shellArgs"] as JsonArray ?? []) {
				if (argument?.GetValue<string>() is { } value) arguments.Add(value.Contains(' ') ? $"\"{value}\"" : value);
			}

			profile = new TerminalProfile(name, shell, string.Join(' ', arguments));
		}
		else {
			TerminalProfile fallback = TerminalProfiles.Default(_workbench.Panel.Profiles, _workbench.Panel.DefaultProfile);
			profile = fallback with { Name = name };
		}

		string? cwd = parameters["cwd"]?.GetValue<string>();
		bool hidden = parameters["hideFromUser"]?.GetValue<bool>() == true;

		TerminalSession? session = _workbench.Panel.CreateTerminal(profile, cwd, code => _post(() => {
			_terminals.Remove(id);
			_bridge.Notify("terminal/closed", new JsonObject { ["id"] = id, ["code"] = code });
		}), show: !hidden);

		if (session is null) {
			_bridge.Notify("terminal/closed", new JsonObject { ["id"] = id, ["code"] = -1 });
			return;
		}

		_terminals[id] = session;
	}

	#endregion
}
