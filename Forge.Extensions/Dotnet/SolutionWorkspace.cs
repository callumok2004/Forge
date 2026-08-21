using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Dotnet;

public sealed class SolutionWorkspace
{
	private static readonly string[] SolutionExtensions = [".sln", ".slnx", ".slnf"];
	private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

	private static readonly string[] Excluded = ["node_modules", "bin", "obj", ".git", ".vs"];

	private const int MaxAutoLoad = 100;

	private const int MaxSolutionCandidates = 32;

	public string? Solution { get; private set; }

	public string[] Projects { get; private set; } = [];

	public string? Target => Solution ?? (Projects.Length > 0 ? Projects[0] : null);

	public void Reload(string? root, string? configuredSolution = null) {
		Solution = null;
		Projects = [];

		if (root is null || !Directory.Exists(root)) return;

		if (configuredSolution is { Length: > 0 }) {
			string configured = Path.IsPathRooted(configuredSolution)
				? configuredSolution
				: Path.Combine(root, configuredSolution);

			if (File.Exists(configured)) {
				Solution = configured;
				return;
			}
		}

		List<string> solutions = [];
		List<string> projects = [];

		Walk(root, solutions, projects);

		if (solutions.Count > 0) {
			solutions.Sort(Rank);
			Solution = solutions[0];
			return;
		}

		projects.Sort(StringComparer.OrdinalIgnoreCase);
		Projects = [.. projects];
	}

	private static void Walk(string directory, List<string> solutions, List<string> projects) {
		if (solutions.Count >= MaxSolutionCandidates && projects.Count >= MaxAutoLoad) return;

		try {
			foreach (string file in Directory.EnumerateFiles(directory)) {
				string extension = Path.GetExtension(file);

				if (Array.IndexOf(SolutionExtensions, extension.ToLowerInvariant()) >= 0) {
					if (solutions.Count < MaxSolutionCandidates) solutions.Add(file);
				}
				else if (Array.IndexOf(ProjectExtensions, extension.ToLowerInvariant()) >= 0 &&
					projects.Count < MaxAutoLoad) projects.Add(file);
			}

			foreach (string child in Directory.EnumerateDirectories(directory)) {
				string name = Path.GetFileName(child);

				if (Array.IndexOf(Excluded, name) < 0) Walk(child, solutions, projects);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private static int Rank(string left, string right) {
		int depth = Depth(left).CompareTo(Depth(right));

		if (depth != 0) return depth;

		int extension = Preference(left).CompareTo(Preference(right));

		return extension != 0 ? extension : string.CompareOrdinal(left, right);
	}

	private static int Depth(string path) => path.Count(character => character == Path.DirectorySeparatorChar);

	private static int Preference(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
		".slnx" => 0,
		".sln" => 1,
		_ => 2,
	};
}
