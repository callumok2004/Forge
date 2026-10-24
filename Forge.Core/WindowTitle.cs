using System.Text;

namespace Forge.Core;

public static class WindowTitle
{
	public const string DefaultTemplate = "${dirty}${activeEditorShort}${separator}${rootName}${separator}${profileName}${separator}${appName}";

	private const string Separator = " - ";
	private const string SeparatorToken = "${separator}";

	public static string Format(string? template, string rootPath, string? activePath, bool dirty) {
		string rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		string? activeFolder = activePath is null ? null : Path.GetDirectoryName(activePath);

		string Resolve(string name) => name switch {
			"activeEditorShort" => activePath is null ? string.Empty : Path.GetFileName(activePath),
			"activeEditorMedium" => activePath is null ? string.Empty : Path.GetRelativePath(rootPath, activePath),
			"activeEditorLong" => activePath ?? string.Empty,
			"activeFolderShort" => activeFolder is null ? string.Empty : Path.GetFileName(activeFolder),
			"activeFolderMedium" => activeFolder is null ? string.Empty : Path.GetRelativePath(rootPath, activeFolder),
			"activeFolderLong" => activeFolder ?? string.Empty,
			"rootName" or "folderName" or "rootNameShort" => rootName,
			"rootPath" or "folderPath" => rootPath,
			"appName" => "Forge",
			"dirty" => dirty ? "● " : string.Empty,
			_ => string.Empty,
		};

		List<string> segments = [];

		foreach (string segment in (template ?? DefaultTemplate).Split(SeparatorToken)) {
			string resolved = Substitute(segment, Resolve);

			if (resolved.Length > 0) segments.Add(resolved);
		}

		return string.Join(Separator, segments);
	}

	private static string Substitute(string text, Func<string, string> resolve) {
		StringBuilder builder = new(text.Length);
		int index = 0;

		while (index < text.Length) {
			int start = text.IndexOf("${", index, StringComparison.Ordinal);
			int end = start < 0 ? -1 : text.IndexOf('}', start + 2);

			if (end < 0) {
				builder.Append(text, index, text.Length - index);
				break;
			}

			builder.Append(text, index, start - index);
			builder.Append(resolve(text[(start + 2)..end]));
			index = end + 1;
		}

		string result = builder.ToString();
		return string.IsNullOrWhiteSpace(result) ? string.Empty : result;
	}
}
