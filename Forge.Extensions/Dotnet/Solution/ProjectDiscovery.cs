namespace Forge.Extensions.Dotnet.Solution;

public static class ProjectDiscovery
{
	private static readonly string[] WorkspacePatterns = ["*.slnx", "*.sln", "*.slnf", "*.csproj", "*.fsproj", "*.vbproj"];

	private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

	public static bool IsSource(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

	public static bool ContainsProject(string directory, int depth) {
		try {
			foreach (string pattern in WorkspacePatterns) {
				if (Directory.EnumerateFiles(directory, pattern).Any()) return true;
			}

			if (depth <= 0) return false;

			foreach (string child in Directory.EnumerateDirectories(directory)) {
				if (Path.GetFileName(child) is "bin" or "obj" or "node_modules" or ".git" or ".vs") continue;

				if (ContainsProject(child, depth - 1)) return true;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}

		return false;
	}

	public static string? ProjectOf(string file) {
		for (string? current = Path.GetDirectoryName(file); current is not null; current = Path.GetDirectoryName(current)) {
			try {
				foreach (string pattern in ProjectPatterns) {
					string[] matches = Directory.GetFiles(current, pattern);

					if (matches.Length == 0) continue;

					Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
					return matches[0];
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			}
		}

		return null;
	}
}
