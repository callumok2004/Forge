using System.Text;
using System.Text.Json.Nodes;

namespace Forge.Debug;

public sealed record ResolverContext(string WorkspaceFolder)
{
	public string? File { get; init; }

	public int LineNumber { get; init; }

	public int ColumnNumber { get; init; }

	public string? SelectedText { get; init; }

	public string? ExecPath { get; init; }
}

public static class VariableResolver
{
	public static JsonNode? Resolve(JsonNode? node, ResolverContext context) {
		switch (node) {
			case JsonValue value when value.TryGetValue(out string? text):
				return JsonValue.Create(Resolve(text, context));

			case JsonArray array: {
				JsonArray resolved = [];

				foreach (JsonNode? entry in array) resolved.Add(Resolve(entry, context));

				return resolved;
			}

			case JsonObject o: {
				JsonObject resolved = [];

				foreach (KeyValuePair<string, JsonNode?> pair in o) {
					resolved[pair.Key] = Resolve(pair.Value, context);
				}

				return resolved;
			}

			default:
				return node?.DeepClone();
		}
	}

	public static string Resolve(string text, ResolverContext context) {
		if (text.IndexOf("${", StringComparison.Ordinal) < 0) return text;

		StringBuilder result = new(text.Length);

		for (int i = 0; i < text.Length; i++) {
			if (text[i] != '$' || i + 1 >= text.Length || text[i + 1] != '{') {
				result.Append(text[i]);
				continue;
			}

			int close = text.IndexOf('}', i + 2);

			if (close < 0) {
				result.Append(text.AsSpan(i));
				break;
			}

			string token = text[(i + 2)..close];
			result.Append(Substitute(token, context));
			i = close;
		}

		return result.ToString();
	}

	private static string Substitute(string token, ResolverContext context) {
		string name = token;
		string? argument = null;

		int colon = token.IndexOf(':');

		if (colon > 0) {
			name = token[..colon];
			argument = token[(colon + 1)..];
		}

		string file = context.File ?? string.Empty;
		string folder = context.WorkspaceFolder;

		return name switch {
			"workspaceFolder" => folder,
			"workspaceRoot" => folder,
			"cwd" => folder,
			"workspaceFolderBasename" or "workspaceRootFolderName" => Basename(folder),
			"userHome" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"file" => file,
			"fileWorkspaceFolder" => folder,
			"fileWorkspaceFolderBasename" => Basename(folder),
			"relativeFile" => Relative(folder, file),
			"relativeFileDirname" => RelativeDirectory(folder, file),
			"fileDirname" => Path.GetDirectoryName(file) ?? string.Empty,
			"fileExtname" => Path.GetExtension(file),
			"fileBasename" => Path.GetFileName(file),
			"fileBasenameNoExtension" => Path.GetFileNameWithoutExtension(file),
			"fileDirnameBasename" => Basename(Path.GetDirectoryName(file) ?? string.Empty),
			"lineNumber" => context.LineNumber.ToString(),
			"columnNumber" => context.ColumnNumber.ToString(),
			"selectedText" => context.SelectedText ?? string.Empty,
			"execPath" => context.ExecPath ?? Environment.ProcessPath ?? string.Empty,
			"execInstallFolder" => AppContext.BaseDirectory,
			"pathSeparator" or "/" => Path.DirectorySeparatorChar.ToString(),
			"env" => argument is { Length: > 0 } ? Environment.GetEnvironmentVariable(argument) ?? string.Empty : string.Empty,
			_ => $"${{{token}}}",
		};
	}

	private static string Basename(string path) =>
		Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

	private static string Relative(string folder, string file) {
		if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(folder)) return file;

		try {
			return Path.GetRelativePath(folder, file);
		}
		catch (ArgumentException) {
			return file;
		}
	}

	private static string RelativeDirectory(string folder, string file) {
		string directory = Path.GetDirectoryName(file) ?? string.Empty;
		string relative = Relative(folder, directory);

		return relative.Length == 0 ? "." : relative;
	}
}
