using System.Text.Json.Nodes;

namespace Forge.VSCode;

public sealed record HostEditor(string Path, int Line, int Column, int SelectionEndLine, int SelectionEndColumn)
{
	public JsonObject ToJson() => new() {
		["document"] = new JsonObject {
			["uri"] = UriFor(Path),
			["fileName"] = Path,
			["languageId"] = LanguageOf(Path),
			["isUntitled"] = false,
		},
		["selection"] = new JsonObject {
			["start"] = new JsonObject { ["line"] = Line, ["character"] = Column },
			["end"] = new JsonObject { ["line"] = SelectionEndLine, ["character"] = SelectionEndColumn },
			["active"] = new JsonObject { ["line"] = SelectionEndLine, ["character"] = SelectionEndColumn },
			["anchor"] = new JsonObject { ["line"] = Line, ["character"] = Column },
			["isEmpty"] = Line == SelectionEndLine && Column == SelectionEndColumn,
		},
	};

	public static JsonObject UriFor(string path) {
		string normalised = path.Replace('\\', '/');

		return new JsonObject {
			["$mid"] = "uri",
			["scheme"] = "file",
			["authority"] = string.Empty,
			["path"] = normalised.StartsWith('/') ? normalised : "/" + normalised,
			["fsPath"] = path,
		};
	}

	private static string LanguageOf(string path) {
		string extension = System.IO.Path.GetExtension(path);

		return extension.Length > 1 ? extension[1..] : "plaintext";
	}
}

public sealed class HostState
{
	public string? WorkspacePath { get; set; }

	public HostEditor? ActiveEditor { get; set; }

	public IReadOnlyList<HostEditor> VisibleEditors { get; set; } = [];

	public IReadOnlyDictionary<string, string> Configuration { get; set; } = new Dictionary<string, string>();

	public JsonObject ToJson() {
		JsonArray visible = [];

		foreach (HostEditor editor in VisibleEditors) visible.Add(editor.ToJson());

		JsonObject configuration = [];

		foreach ((string key, string value) in Configuration) configuration[key] = value;

		JsonArray folders = [];

		if (WorkspacePath is { Length: > 0 } root) {
			folders.Add(new JsonObject {
				["name"] = System.IO.Path.GetFileName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
				["uri"] = HostEditor.UriFor(root),
				["index"] = 0,
			});
		}

		return new JsonObject {
			["workspaceFolders"] = folders.Count > 0 ? folders : null,
			["activeTextEditor"] = ActiveEditor?.ToJson(),
			["visibleTextEditors"] = visible,
			["configuration"] = configuration,
		};
	}
}
