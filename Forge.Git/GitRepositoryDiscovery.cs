namespace Forge.Git;

public sealed record GitDiscoveryOptions
{
	public string AutoRepositoryDetection { get; init; } = "true";

	public int RepositoryScanMaxDepth { get; init; } = 1;

	public IReadOnlyList<string> RepositoryScanIgnoredFolders { get; init; } = ["node_modules"];

	public IReadOnlyList<string> ScanRepositories { get; init; } = [];

	public bool DetectSubmodules { get; init; } = true;

	public int DetectSubmodulesLimit { get; init; } = 10;

	public IReadOnlyList<string> IgnoredRepositories { get; init; } = [];

	public string OpenRepositoryInParentFolders { get; init; } = "prompt";

	public bool ScansSubfolders => AutoRepositoryDetection is "true" or "subFolders";

	public bool ScansOpenEditors => AutoRepositoryDetection is "true" or "openEditors";
}

public sealed class GitRepositoryDiscovery(string workspace, GitDiscoveryOptions options, Func<string, string?> findRoot, IReadOnlySet<string>? closed = null, IReadOnlySet<string>? trustedParents = null)
{
	private readonly string _workspace = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar);
	private readonly List<string> _roots = [];
	private readonly List<string> _parents = [];
	private readonly List<(string Repository, int Count)> _tooManySubmodules = [];

	public IReadOnlyList<string> Roots => _roots;

	public IReadOnlyList<string> ParentRepositories => _parents;

	public IReadOnlyList<(string Repository, int Count)> TooManySubmodules => _tooManySubmodules;

	public void Run() {
		Open(_workspace);

		if (!options.ScansSubfolders) return;

		HashSet<string> subfolders = [.. Traverse(_workspace, options.RepositoryScanMaxDepth, options.RepositoryScanIgnoredFolders)];

		foreach (string scan in options.ScanRepositories) {
			if (scan == ".git" || Path.IsPathRooted(scan)) continue;

			subfolders.Add(Path.GetFullPath(Path.Combine(_workspace, scan)));
		}

		foreach (string folder in subfolders) {
			if (HasDotGit(folder)) Open(folder);
		}
	}

	public string? OpenFor(string filePath) {
		string full = Path.GetFullPath(filePath);

		if (!options.ScansOpenEditors || Containing(full) is not null || Path.GetDirectoryName(full) is not { } directory || !Directory.Exists(directory)) return null;

		int before = _roots.Count;
		Open(directory);

		return _roots.Count > before ? _roots[before] : null;
	}

	public bool Open(string path, bool openIfClosed = false, bool openIfParent = false) {
		string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

		if (Exact(full) is not null) return false;

		if (Containing(full) is { } containing && !Same(containing, full) && !HasDotGit(full)) return false;

		if (findRoot(full) is not { } found) return false;

		string root = Path.GetFullPath(found).TrimEnd(Path.DirectorySeparatorChar);

		if (Exact(root) is not null || Ignored(root)) return false;

		if (options.OpenRepositoryInParentFolders != "always" && trustedParents?.Contains(root) != true && !openIfParent && Outside(root)) {
			if (!_parents.Exists(parent => Same(parent, root))) _parents.Add(root);

			return false;
		}

		if (!openIfClosed && closed?.Contains(root) == true) return false;

		_roots.Add(root);
		OpenSubmodules(root);
		return true;
	}

	private void OpenSubmodules(string root) {
		if (!options.DetectSubmodules) return;

		List<string> submodules = Submodules(root);

		if (submodules.Count > options.DetectSubmodulesLimit) _tooManySubmodules.Add((root, submodules.Count));

		foreach (string submodule in submodules.Take(options.DetectSubmodulesLimit)) {
			string full = Path.GetFullPath(Path.Combine(root, submodule));

			if (HasDotGit(full)) Open(full);
		}
	}

	public static List<string> Submodules(string root) {
		List<string> paths = [];
		string file = Path.Combine(root, ".gitmodules");

		if (!File.Exists(file)) return paths;

		try {
			foreach (string raw in File.ReadLines(file)) {
				ReadOnlySpan<char> line = raw.AsSpan().Trim();

				if (!line.StartsWith("path", StringComparison.Ordinal)) continue;

				ReadOnlySpan<char> rest = line[4..].TrimStart();

				if (rest is not ['=', ..]) continue;

				ReadOnlySpan<char> value = rest[1..].Trim();

				if (value.Length > 0) paths.Add(value.ToString().Replace('/', Path.DirectorySeparatorChar));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}

		return paths;
	}

	public static List<string> Traverse(string workspace, int maxDepth, IReadOnlyList<string> ignored) {
		List<string> result = [];
		Queue<(string Path, int Depth)> pending = new();
		pending.Enqueue((workspace, 0));

		while (pending.TryDequeue(out (string Path, int Depth) current)) {
			string[] children;

			try {
				children = Directory.GetDirectories(current.Path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				continue;
			}

			if (current.Depth != 0) result.Add(current.Path);

			if (current.Depth >= maxDepth && maxDepth != -1) continue;

			foreach (string child in children) {
				string name = Path.GetFileName(child);

				if (name == ".git" || ignored.Any(folder => string.Equals(folder, name, StringComparison.OrdinalIgnoreCase))) continue;

				pending.Enqueue((child, current.Depth + 1));
			}
		}

		return result;
	}

	private static bool HasDotGit(string folder) {
		string dotGit = Path.Combine(folder, ".git");
		return Directory.Exists(dotGit) || File.Exists(dotGit);
	}

	private bool Ignored(string root) {
		foreach (string ignored in options.IgnoredRepositories) {
			string candidate = Path.IsPathRooted(ignored) ? ignored : Path.Combine(_workspace, ignored);

			if (Same(Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar), root)) return true;
		}

		return false;
	}

	private bool Outside(string root) => !IsInside(root, _workspace);

	private static bool IsInside(string path, string folder) => Same(path, folder) || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	private string? Exact(string root) => _roots.Find(existing => Same(existing, root));

	public void Forget(string root) => _roots.RemoveAll(existing => Same(existing, root));

	public string? Containing(string path) {
		string? best = null;

		foreach (string root in _roots) {
			if (IsInside(path, root) && (best is null || root.Length > best.Length)) best = root;
		}

		return best;
	}

	private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
