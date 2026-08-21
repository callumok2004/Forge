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

	public string[] EditorFontFamily { get; private set; } = ["Consolas", "Courier New"];

	public float EditorFontSize { get; private set; } = 14f;

	public float EditorLineHeight { get; private set; }

	public string[] UiFontFamily { get; private set; } = ["Segoe WPC", "Segoe UI", "Tahoma"];

	public float UiFontSize { get; private set; } = 13f;

	public bool MinimapEnabled { get; private set; } = true;

	public string? DefaultTerminalProfile { get; private set; }

	public int TabSize { get; private set; } = 4;

	public bool SuggestDetailsVisible { get; private set; }

	public void SetSuggestDetailsVisible(bool visible) {
		if (visible == SuggestDetailsVisible) return;

		SuggestDetailsVisible = visible;
		_dirty = true;
		Save();
	}

	public void SetMinimapEnabled(bool enabled) {
		if (enabled == MinimapEnabled) return;

		MinimapEnabled = enabled;
		_dirty = true;
		Save();
	}

	public void AdjustZoom(float delta) => SetZoom(ZoomLevel + delta);

	public void ResetZoom() => SetZoom(0f);

	private void SetZoom(float level) {
		float clamped = Math.Clamp(level, MinZoomLevel, MaxZoomLevel);

		if (clamped == ZoomLevel) return;

		ZoomLevel = clamped;
		_dirty = true;

		Save();
	}

	public void Save() {
		if (!_dirty || _path is null) return;

		try {
			if (MinimapEnabled) _raw.Remove("editor.minimap.enabled");
			else _raw["editor.minimap.enabled"] = false;

			if (SuggestDetailsVisible) _raw["editor.suggest.expandDocs"] = true;
			else _raw.Remove("editor.suggest.expandDocs");

			if (ZoomLevel == 0f) _raw.Remove("window.zoomLevel");
			else _raw["window.zoomLevel"] = MathF.Round(ZoomLevel, 2);

			string json = _raw.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

			string temporary = _path + ".tmp";
			File.WriteAllText(temporary, json);
			File.Move(temporary, _path, overwrite: true);

			_dirty = false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or NotSupportedException) {
		}
	}

	public static Settings Load(string path) {
		Settings settings = new() { _path = path };

		try {
			if (File.Exists(path)) {
				JsonNodeOptions nodeOptions = new() { PropertyNameCaseInsensitive = false };
				JsonDocumentOptions documentOptions = new() {
					CommentHandling = JsonCommentHandling.Skip,
					AllowTrailingCommas = true,
				};

				if (JsonNode.Parse(File.ReadAllText(path), nodeOptions, documentOptions)
					is JsonObject root) {
					settings._raw = root;
					settings.Apply(root);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException
			or UnauthorizedAccessException) {
			settings._path = null;
		}

		Current = settings;
		return settings;
	}

	private void Apply(JsonObject root) {
		ZoomLevel = Math.Clamp(Number(root, "window.zoomLevel", ZoomLevel), MinZoomLevel, MaxZoomLevel);
		EditorFontSize = Number(root, "editor.fontSize", EditorFontSize);
		EditorLineHeight = Number(root, "editor.lineHeight", EditorLineHeight);
		UiFontSize = Number(root, "window.uiFontSize", UiFontSize);
		TabSize = (int)Number(root, "editor.tabSize", TabSize);

		EditorFontFamily = FontStack(root, "editor.fontFamily", EditorFontFamily);
		UiFontFamily = FontStack(root, "window.uiFontFamily", UiFontFamily);
		MinimapEnabled = Boolean(root, "editor.minimap.enabled", MinimapEnabled);
		SuggestDetailsVisible = Boolean(root, "editor.suggest.expandDocs", SuggestDetailsVisible);

		DefaultTerminalProfile = root["terminal.integrated.defaultProfile.windows"]
			?.GetValue<string>() ?? DefaultTerminalProfile;
	}

	public string? GetString(string key) =>
		_raw[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

	public bool? GetBoolean(string key) =>
		_raw[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

	public int? GetInt32(string key) =>
		_raw[key] is JsonValue value && value.TryGetValue(out double number) ? (int)number : null;

	public string[]? GetStringArray(string key) {
		if (_raw[key] is not JsonArray array) return null;

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
