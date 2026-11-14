using System.Xml;

namespace Forge.Extensions.Dotnet.Solution;

public static class SharedSourceProjects
{
	public static IReadOnlyList<string>? ProjectsToOpen(string solutionPath, out IReadOnlyList<string> skipped) {
		skipped = [];

		if (!solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || !File.Exists(solutionPath)) return null;

		List<(string Path, bool Builds)> projects;

		try {
			projects = ReadSlnx(File.ReadAllText(solutionPath), Path.GetDirectoryName(solutionPath)!);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException) {
			return null;
		}

		List<string> building = [.. projects.Where(static project => project.Builds).Select(static project => project.Path)];
		List<string> excluded = [];

		foreach ((string path, bool builds) in projects) {
			if (builds) continue;

			string directory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar;

			if (building.Any(other => CompilesFrom(other, directory))) excluded.Add(path);
		}

		if (excluded.Count == 0) return null;

		skipped = excluded;
		return [.. projects.Select(static project => project.Path).Where(path => !excluded.Contains(path, StringComparer.OrdinalIgnoreCase))];
	}

	internal static List<(string Path, bool Builds)> ReadSlnx(string text, string baseDirectory) {
		List<(string, bool)> projects = [];
		XmlDocument document = new() { XmlResolver = null };

		using (XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true })) document.Load(reader);

		foreach (XmlElement project in document.GetElementsByTagName("Project").OfType<XmlElement>()) {
			if (project.GetAttribute("Path") is not { Length: > 0 } relative) continue;

			bool builds = true;

			foreach (XmlElement build in project.ChildNodes.OfType<XmlElement>()) {
				if (build.LocalName != "Build" || build.HasAttribute("Solution")) continue;

				if (string.Equals(build.GetAttribute("Project"), "false", StringComparison.OrdinalIgnoreCase)) builds = false;
			}

			projects.Add((Path.GetFullPath(Path.Combine(baseDirectory, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))), builds));
		}

		return projects;
	}

	internal static bool CompilesFrom(string projectPath, string directory) {
		string text;

		try {
			text = File.ReadAllText(projectPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return false;
		}

		return CompilesFrom(text, Path.GetDirectoryName(projectPath)!, directory);
	}

	internal static bool CompilesFrom(string projectText, string projectDirectory, string directory) {
		XmlDocument document = new() { XmlResolver = null };

		try {
			using XmlReader reader = XmlReader.Create(new StringReader(projectText), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true });
			document.Load(reader);
		}
		catch (XmlException) {
			return false;
		}

		foreach (XmlElement compile in document.GetElementsByTagName("Compile").OfType<XmlElement>()) {
			foreach (string include in compile.GetAttribute("Include").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
				if (include.Contains("$(", StringComparison.Ordinal) || include.Contains("@(", StringComparison.Ordinal)) continue;

				int wildcard = include.IndexOfAny(['*', '?']);
				string fixedPart = wildcard < 0 ? include : include[..wildcard];
				string resolved = Path.GetFullPath(Path.Combine(projectDirectory, fixedPart.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

				if (wildcard >= 0 && !resolved.EndsWith(Path.DirectorySeparatorChar)) resolved = Path.GetDirectoryName(resolved) + Path.DirectorySeparatorChar;

				if (resolved.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) return true;
			}
		}

		return false;
	}
}
