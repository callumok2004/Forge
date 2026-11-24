using Forge.Common.Git;

namespace Forge.Git;

public sealed class GitRepositories(string workspace, GitDiscoveryOptions options, IReadOnlySet<string> closed, IReadOnlySet<string> trustedParents, Func<string, string?> findRoot) : IGitRepositories
{
	private readonly Lock _gate = new();
	private readonly HashSet<string> _closed = new(closed, StringComparer.OrdinalIgnoreCase);
	private GitRepositoryDiscovery? _discovery;
	private IGitRepository[] _repositories = [];
	private Action? _changed;

	public GitRepositories(string workspace, GitDiscoveryOptions options, IReadOnlySet<string> closed, IReadOnlySet<string> trustedParents) : this(workspace, options, closed, trustedParents, GitProcess.FindRepositoryRoot) {
	}

	public IReadOnlyList<IGitRepository> GetAll() => _repositories;

	public IReadOnlyCollection<string> Closed {
		get {
			lock (_gate) return [.. _closed];
		}
	}

	public IReadOnlyList<string> ParentRepositories {
		get {
			lock (_gate) return _discovery is null ? [] : [.. _discovery.ParentRepositories.Where(parent => !Contains(parent))];
		}
	}

	public IReadOnlyList<(string Repository, int Count)> TooManySubmodules {
		get {
			lock (_gate) return _discovery is null ? [] : [.. _discovery.TooManySubmodules];
		}
	}

	public Action? ClosedChanged { get; set; }

	public void OnChanged(Action handler) {
		_changed -= handler;
		_changed += handler;
	}

	public void Discover() {
		if (!GitProcess.IsAvailable || !Directory.Exists(workspace)) return;

		GitRepositoryDiscovery discovery = new(workspace, options, findRoot, _closed, trustedParents);
		discovery.Run();

		lock (_gate) _discovery = discovery;

		Sync();
	}

	public IGitRepository? OpenFor(string filePath) {
		string? root;

		lock (_gate) root = _discovery?.OpenFor(filePath);

		if (root is null) return null;

		Sync();
		return Find(root);
	}

	public IGitRepository? Open(string path, bool openIfParent) {
		string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
		bool reopened;

		lock (_gate) {
			if (_discovery is null) return null;

			reopened = _closed.Remove(full);
			_discovery.Open(full, openIfClosed: true, openIfParent: openIfParent);
		}

		if (reopened) ClosedChanged?.Invoke();

		Sync();
		return Find(full);
	}

	public void Close(IGitRepository repository) {
		string root = repository.GetRootPath();

		lock (_gate) {
			if (Array.IndexOf(_repositories, repository) < 0) return;

			_repositories = [.. _repositories.Where(existing => !ReferenceEquals(existing, repository))];
			_closed.Add(root);
			_discovery?.Forget(root);
		}

		ClosedChanged?.Invoke();
		_changed?.Invoke();
	}

	public IGitRepository? For(string path) {
		IGitRepository? best = null;
		string full = Path.GetFullPath(path);

		foreach (IGitRepository repository in _repositories) {
			string root = repository.GetRootPath();

			if (!Inside(full, root)) continue;

			if (best is null || root.Length > best.GetRootPath().Length) best = repository;
		}

		return best;
	}

	public void RefreshFor(IReadOnlyCollection<string> paths) {
		HashSet<IGitRepository> refreshed = [];

		foreach (string path in paths) {
			if (Owner(path) is { } repository && refreshed.Add(repository)) repository.Refresh();
		}
	}

	public void OpenPossible(IReadOnlyCollection<string> paths) {
		if (options.AutoRepositoryDetection == "false") return;

		HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);

		foreach (string path in paths) {
			string full = Path.GetFullPath(path);
			int marker = full.IndexOf(Path.DirectorySeparatorChar + ".git", StringComparison.OrdinalIgnoreCase);

			if (marker < 0) continue;

			int end = marker + 5;

			if (end < full.Length && full[end] != Path.DirectorySeparatorChar) continue;

			string root = full[..marker];

			if (Find(root) is null && Directory.Exists(root)) candidates.Add(root);
		}

		bool opened = false;

		foreach (string root in candidates) {
			lock (_gate) opened |= _discovery?.Open(root) == true;
		}

		if (opened) Sync();
	}

	public IGitRepository? Owner(string path) {
		string full = Path.GetFullPath(path);
		int marker = full.IndexOf(Path.DirectorySeparatorChar + ".git", StringComparison.OrdinalIgnoreCase);

		while (marker >= 0) {
			int end = marker + 5;

			if (end == full.Length || full[end] == Path.DirectorySeparatorChar) break;

			marker = full.IndexOf(Path.DirectorySeparatorChar + ".git", end, StringComparison.OrdinalIgnoreCase);
		}

		if (marker < 0) return For(full);

		string root = full[..marker];
		ReadOnlySpan<char> rest = full.AsSpan(Math.Min(full.Length, marker + 6));

		while (rest.StartsWith("modules" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
			ReadOnlySpan<char> afterModules = rest[8..];
			int next = ModuleEnd(afterModules);

			if (next < 0) break;

			root = Path.Combine(root, afterModules[..next].ToString());
			rest = afterModules[(next + 1)..];
		}

		return Find(root);
	}

	private static int ModuleEnd(ReadOnlySpan<char> path) {
		int offset = 0;

		while (offset < path.Length) {
			int separator = path[offset..].IndexOf(Path.DirectorySeparatorChar);
			ReadOnlySpan<char> segment = separator < 0 ? path[offset..] : path.Slice(offset, separator);

			if (offset > 0 && IsGitDirEntry(segment)) return offset - 1;

			if (separator < 0) return -1;

			offset += separator + 1;
		}

		return -1;
	}

	private static bool IsGitDirEntry(ReadOnlySpan<char> segment) => segment is "HEAD" or "index" or "refs" or "objects" or "logs" or "info" or "hooks" or "modules" or "packed-refs" or "config" or "FETCH_HEAD" or "ORIG_HEAD" or "MERGE_HEAD";

	private IGitRepository? Find(string root) {
		string trimmed = root.TrimEnd(Path.DirectorySeparatorChar);

		foreach (IGitRepository repository in _repositories) {
			if (string.Equals(repository.GetRootPath(), trimmed, StringComparison.OrdinalIgnoreCase)) return repository;
		}

		return null;
	}

	private bool Contains(string root) => Find(root) is not null;

	private void Sync() {
		bool changed = false;

		lock (_gate) {
			if (_discovery is null) return;

			List<IGitRepository> next = [.. _repositories];

			foreach (string root in _discovery.Roots) {
				if (next.Exists(existing => string.Equals(existing.GetRootPath(), root, StringComparison.OrdinalIgnoreCase))) continue;

				GitRepository repository = GitRepository.At(root);
				next.Add(repository);
				repository.Refresh();
				changed = true;
			}

			if (changed) _repositories = [.. next];
		}

		if (changed) _changed?.Invoke();
	}

	private static bool Inside(string path, string root) => path.Length >= root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar);
}
