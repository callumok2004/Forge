using System.Text.Json;

namespace Forge.VSCode;

public sealed record VSCodeCommand(string Id, string Title, string? Category);

public sealed record VSCodeExtension(
	string Identifier,
	string DisplayName,
	string Directory,
	string[] ActivationEvents,
	bool HasWebview,
	VSCodeCommand[] Commands);

public static class VSCodeExtensions
{
	public static string DefaultDirectory => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge", "vscode-extensions");

	public static List<VSCodeExtension> Discover(params string[] directories) {
		List<VSCodeExtension> found = [];
		HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];

		foreach (string root in directories) {
			if (!Directory.Exists(root)) continue;

			try {
				foreach (string directory in Directory.EnumerateDirectories(root)) {
					if (Read(directory) is { } extension && seen.Add(extension.Identifier)) found.Add(extension);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			}
		}

		return found;
	}

	public static VSCodeExtension? Read(string directory) {
		string manifest = Path.Combine(directory, "package.json");

		try {
			if (!File.Exists(manifest)) return null;

			using FileStream stream = File.OpenRead(manifest);
			using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions {
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			});

			JsonElement root = document.RootElement;

			if (Text(root, "name") is not { } name) return null;

			if (Text(root, "main") is null) return null;

			string publisher = Text(root, "publisher") ?? "unknown";

			return new VSCodeExtension(
				$"{publisher}.{name}",
				Text(root, "displayName") ?? name,
				directory,
				Strings(root, "activationEvents"),
				HasWebviewView(root),
				ReadCommands(root));
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
			return null;
		}
	}

	private static VSCodeCommand[] ReadCommands(in JsonElement root) {
		if (!root.TryGetProperty("contributes", out JsonElement contributes)) return [];

		if (!contributes.TryGetProperty("commands", out JsonElement commands) || commands.ValueKind != JsonValueKind.Array) return [];

		List<VSCodeCommand> results = [];

		foreach (JsonElement command in commands.EnumerateArray()) {
			if (Text(command, "command") is not { } id) continue;

			results.Add(new VSCodeCommand(id, Text(command, "title") ?? id, Text(command, "category")));
		}

		return [.. results];
	}

	private static bool HasWebviewView(in JsonElement root) {
		if (!root.TryGetProperty("contributes", out JsonElement contributes)) return false;

		if (!contributes.TryGetProperty("views", out JsonElement views) || views.ValueKind != JsonValueKind.Object) return false;

		foreach (JsonProperty container in views.EnumerateObject()) {
			if (container.Value.ValueKind != JsonValueKind.Array) continue;

			foreach (JsonElement view in container.Value.EnumerateArray()) {
				if (Text(view, "type") == "webview") return true;
			}
		}

		return false;
	}

	private static string? Text(in JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static string[] Strings(in JsonElement element, string name) {
		if (!element.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array) return [];

		List<string> results = [];

		foreach (JsonElement item in array.EnumerateArray()) {
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) results.Add(text);
		}

		return [.. results];
	}
}
