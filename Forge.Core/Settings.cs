using System.Text.Json.Nodes;
using System.Text.Json;

namespace Forge.Core;

public sealed class Settings
{
	private const float ZoomStep = 1.2f;

	private const float MinZoomLevel = -8f;
	private const float MaxZoomLevel = 8f;

	public static Settings Current { get; private set; } = new();

	private string? _path;

	private JsonObject _raw = [];

	private bool _dirty;

	public float ZoomLevel { get; private set; }

	public float ZoomFactor => MathF.Pow(ZoomStep, ZoomLevel);

	public string[] EditorFontFamily { get; private set; } = ["JetBrains Mono", "Consolas", "Courier New"];

	public float EditorFontSize { get; private set; } = 14f;

	public float EditorLineHeight { get; private set; }

	public string[] UiFontFamily { get; private set; } = ["Geist", "Segoe WPC", "Segoe UI", "Tahoma"];

	public float UiFontSize { get; private set; } = 13f;

	public bool MinimapEnabled { get; private set; } = true;

	public bool MouseWheelZoom { get; private set; }

	public string? DefaultTerminalProfile { get; private set; }

	public int TabSize { get; private set; } = 4;

	public bool SuggestDetailsVisible { get; private set; }

	public void SetSuggestDetailsVisible(bool visible) => SetValue("editor.suggest.expandDocs", visible ? true : null);

	public void SetMinimapEnabled(bool enabled) => SetValue("editor.minimap.enabled", enabled ? null : false);

	public void SetValue(string key, JsonNode? value) => SetValue(key, value, SettingsTarget.User);

	public void SetValue(string key, JsonNode? value, SettingsTarget target) {
		if (target == SettingsTarget.Workspace) {
			SetWorkspaceValue(key, value);
			return;
		}

		if (JsonNode.DeepEquals(value, _raw[key])) return;

		if (value is null) _raw.Remove(key);
		else _raw[key] = value;

		_dirty = true;
		Save();
		Rebuild();
	}

	public void AdjustZoom(float delta) => SetZoom(ZoomLevel + delta);

	public void ResetZoom() => SetZoom(0f);

	private void SetZoom(float level) {
		float clamped = Math.Clamp(level, MinZoomLevel, MaxZoomLevel);

		if (clamped == ZoomLevel) return;

		SetValue("window.zoomLevel", clamped == 0f ? null : MathF.Round(clamped, 2));
	}

	public void Save() {
		if (!_dirty || _path is null) return;

		if (Write(_path, _raw)) _dirty = false;
	}

	private static bool Write(string path, JsonObject root) {
		try {
			if (Path.GetDirectoryName(path) is { } directory) Directory.CreateDirectory(directory);

			string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
			string temporary = path + ".tmp";
			File.WriteAllText(temporary, json);
			File.Move(temporary, path, overwrite: true);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
			return false;
		}
	}

	public static Settings Load(string path) {
		Settings settings = new() { _path = path };

		try {
			if (Read(path) is { } root) settings._raw = root;
		}
		catch (Exception exception) when (exception is IOException or JsonException
			or UnauthorizedAccessException) {
			settings._path = null;
		}

		settings._effective = (JsonObject)settings._raw.DeepClone();
		settings.Apply(settings._effective, Defaults);
		Current = settings;
		return settings;
	}

	private static readonly Settings Defaults = new();

	private static JsonObject? Read(string path) {
		if (!File.Exists(path)) return null;

		JsonNodeOptions nodeOptions = new() { PropertyNameCaseInsensitive = false };
		JsonDocumentOptions documentOptions = new() {
			CommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
		};

		using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader reader = new(stream);

		return JsonNode.Parse(reader.ReadToEnd(), nodeOptions, documentOptions) as JsonObject;
	}

	public event Action<IReadOnlyList<string>>? Changed;

	public JsonObject Snapshot() => (JsonObject)_effective.DeepClone();

	public IEnumerable<string> Keys => _effective.Select(static pair => pair.Key);

	public string? UserPath => _path;

	public JsonNode? UserValue(string key) => _raw[key];

	public JsonNode? WorkspaceValue(string key) => _workspace[key];

	public bool HasWorkspace => _workspaceRoot is not null;

	public string? WorkspaceSettingsPath => _workspaceRoot is { } root ? Forge.Core.WorkspaceConfig.PathFor(root, "settings.json") : null;

	private JsonObject _effective = [];

	private JsonObject _workspace = [];

	private string? _workspaceRoot;

	public void LoadWorkspace(string? root) {
		_workspaceRoot = root;
		Reload();
	}

	private void SetWorkspaceValue(string key, JsonNode? value) {
		if (WorkspaceSettingsPath is not { } path) return;

		JsonObject file;

		try {
			file = Read(path) ?? [];
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
			return;
		}

		if (JsonNode.DeepEquals(value, file[key])) return;

		if (value is null) file.Remove(key);
		else file[key] = value?.DeepClone();

		if (Write(path, file)) Reload();
	}

	private FileSystemWatcher? _watcher;

	private Timer? _debounce;

	public void Watch(Action<Action> post) {
		if (_path is null || _watcher is not null || Path.GetDirectoryName(_path) is not { } directory) return;

		_debounce = new Timer(_ => post(Reload));
		_watcher = new FileSystemWatcher(directory, Path.GetFileName(_path)) {
			NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
		};

		FileSystemEventHandler changed = (_, _) => _debounce.Change(150, Timeout.Infinite);
		_watcher.Changed += changed;
		_watcher.Created += changed;
		_watcher.Renamed += (_, _) => _debounce.Change(150, Timeout.Infinite);
		_watcher.EnableRaisingEvents = true;
	}

	public void Reload() {
		try {
			if (_path is not null) _raw = Read(_path) ?? [];

			JsonObject workspace = [];

			if (_workspaceRoot is { } root) {
				foreach (string layer in Forge.Core.WorkspaceConfig.Layers(root, "settings.json")) {
					if (Read(layer) is not { } values) continue;

					foreach ((string key, JsonNode? value) in values) workspace[key] = value?.DeepClone();
				}
			}

			_workspace = workspace;
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
			return;
		}

		Rebuild();
	}

	private void Rebuild() {
		JsonObject next = (JsonObject)_raw.DeepClone();

		foreach ((string key, JsonNode? value) in _workspace) next[key] = value?.DeepClone();

		List<string> changed = [];

		foreach ((string key, JsonNode? value) in next) {
			if (!JsonNode.DeepEquals(value, _effective[key])) changed.Add(key);
		}

		foreach ((string key, _) in _effective) {
			if (!next.ContainsKey(key)) changed.Add(key);
		}

		if (changed.Count == 0) return;

		_effective = next;
		Apply(next, Defaults);
		Changed?.Invoke(changed);
	}

	private void Apply(JsonObject root, Settings defaults) {
		ZoomLevel = Math.Clamp(Number(root, "window.zoomLevel", defaults.ZoomLevel), MinZoomLevel, MaxZoomLevel);
		EditorFontSize = Number(root, "editor.fontSize", defaults.EditorFontSize);
		EditorLineHeight = Number(root, "editor.lineHeight", defaults.EditorLineHeight);
		UiFontSize = Number(root, "window.uiFontSize", defaults.UiFontSize);
		TabSize = (int)Number(root, "editor.tabSize", defaults.TabSize);

		EditorFontFamily = FontStack(root, "editor.fontFamily", defaults.EditorFontFamily);
		UiFontFamily = FontStack(root, "window.uiFontFamily", defaults.UiFontFamily);
		MinimapEnabled = Boolean(root, "editor.minimap.enabled", defaults.MinimapEnabled);
		MouseWheelZoom = Boolean(root, "editor.mouseWheelZoom", defaults.MouseWheelZoom);
		SuggestDetailsVisible = Boolean(root, "editor.suggest.expandDocs", defaults.SuggestDetailsVisible);

		DefaultTerminalProfile = root["terminal.integrated.defaultProfile.windows"] is JsonValue profile && profile.TryGetValue(out string? name) ? name : defaults.DefaultTerminalProfile;
	}

	public JsonNode? GetRaw(string key) => _effective[key]?.DeepClone();

	public string? GetString(string key) =>
		_effective[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

	public bool? GetBoolean(string key) =>
		_effective[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

	public int? GetInt32(string key) =>
		_effective[key] is JsonValue value && value.TryGetValue(out double number) ? (int)number : null;

	public string[]? GetStringArray(string key) {
		if (_effective[key] is not JsonArray array) return null;

		List<string> values = [with(array.Count)];

		foreach (JsonNode? item in array) {
			if (item is JsonValue value && value.TryGetValue(out string? text)) values.Add(text);
		}

		return [.. values];
	}

	private static float Number(JsonObject root, string key, float fallback) =>
		root[key] is JsonValue value && value.TryGetValue(out double number) ? (float)number : fallback;

	private static bool Boolean(JsonObject root, string key, bool fallback) =>
		root[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

	private static string[] FontStack(JsonObject root, string key, string[] fallback) {
		if (root[key] is not JsonValue value || !value.TryGetValue(out string? stack) || stack is null) {
			return fallback;
		}

		List<string> families = [];

		foreach (string part in stack.Split(',')) {
			string family = part.Trim().Trim('\'', '"');

			if (family.Length == 0 || IsGenericFamily(family)) continue;

			families.Add(family);
		}

		return families.Count > 0 ? [.. families] : fallback;
	}

	private static bool IsGenericFamily(string family) =>
		family.Equals("monospace", StringComparison.OrdinalIgnoreCase) ||
		family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase) ||
		family.Equals("serif", StringComparison.OrdinalIgnoreCase) ||
		family.Equals("system-ui", StringComparison.OrdinalIgnoreCase);
}

public enum SettingsTarget : byte
{
	User,
	Workspace,
}
