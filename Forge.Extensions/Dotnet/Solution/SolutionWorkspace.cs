using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Dotnet.Solution;

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

	private static readonly EnumerationOptions WalkOptions = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, RecurseSubdirectories = false };

	private static void Walk(string root, List<string> solutions, List<string> projects) {
		List<string> level = [root];
		List<string> next = [];

		while (level.Count > 0) {
			foreach (string directory in level) {
				try {
					foreach (string file in Directory.EnumerateFiles(directory, "*", WalkOptions)) {
						ReadOnlySpan<char> extension = Path.GetExtension(file.AsSpan());

						if (IsAny(extension, SolutionExtensions)) {
							if (solutions.Count < MaxSolutionCandidates) solutions.Add(file);
						}
						else if (IsAny(extension, ProjectExtensions) && projects.Count < MaxAutoLoad) projects.Add(file);
					}

					foreach (string child in Directory.EnumerateDirectories(directory, "*", WalkOptions)) {
						ReadOnlySpan<char> name = Path.GetFileName(child.AsSpan());

						if (!IsExcluded(name)) next.Add(child);
					}
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				}
			}

			if (solutions.Count > 0 || projects.Count >= MaxAutoLoad) return;

			(level, next) = (next, level);
			next.Clear();
		}
	}

	private static bool IsAny(ReadOnlySpan<char> extension, string[] candidates) {
		foreach (string candidate in candidates) {
			if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
		}

		return false;
	}

	private static bool IsExcluded(ReadOnlySpan<char> name) {
		if (name.Length > 0 && name[0] == '.') return true;

		foreach (string excluded in Excluded) {
			if (name.Equals(excluded, StringComparison.OrdinalIgnoreCase)) return true;
		}

		return false;
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
