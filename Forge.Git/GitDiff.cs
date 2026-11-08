using Forge.Common.Git;
using Forge.Text;

namespace Forge.Git;

public sealed class GitDiff : IFileDiff
{
	private static readonly GitChange[] None = [];

	private GitChange[] _changes = None;

	private string? _path;
	private string? _index;
	private string? _head;
	private int _version = -1;
	private int _generation;
	private int _syncedGeneration = -1;

	public IReadOnlyList<GitChange> GetChanges() => _changes;

	public string? GetPath() => _path;

	public void Refresh(IGitRepository repository, string fullPath) {
		string root = repository.GetRootPath();
		string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
		string? index = null;
		string? head = null;

		bool inside = !relative.StartsWith("../", StringComparison.Ordinal) && relative != ".." && !Path.IsPathRooted(relative);

		if (inside && !relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) && !IsMerging(repository, fullPath)) {
			index = WithoutBom(GitProcess.Run(root, "show", ":" + relative));

			if (IsStaged(repository, fullPath)) head = WithoutBom(GitProcess.Run(root, "show", "HEAD:" + relative)) ?? string.Empty;
		}

		lock (this) {
			_path = fullPath;
			_index = index;
			_head = head;
			_generation++;
		}
	}

	public void Sync(int version, Func<string> text) {
		string? index;
		string? head;
		int generation;

		lock (this) {
			if (version == _version && _syncedGeneration == _generation) return;

			index = _index;
			head = _head;
			generation = _generation;
		}

		GitChange[] changes = None;

		if (index is not null || head is not null) {
			string current = text();
			List<GitChange> result = [];
			List<LineHunk> primary = index is null ? [] : LineDiff.Compute(index, current);

			foreach (LineHunk hunk in primary) result.Add(ToChange(hunk, false));

			if (head is not null) {
				foreach (LineHunk hunk in LineDiff.Compute(head, current)) {
					if (!Overlaps(primary, hunk)) result.Add(ToChange(hunk, true));
				}

				result.Sort(static (a, b) => a.Line.CompareTo(b.Line));
			}

			if (result.Count > 0) changes = [.. result];
		}

		lock (this) {
			if (generation != _generation) return;

			_changes = changes;
			_version = version;
			_syncedGeneration = generation;
		}
	}

	public void Clear() {
		lock (this) {
			_changes = None;
			_path = null;
			_index = null;
			_head = null;
			_generation++;
		}
	}

	public GitChange? At(int line) {
		GitChange[] changes = _changes;
		GitChange? found = null;

		for (int i = FirstAtOrAfter(changes, line); i < changes.Length && changes[i].Line <= line; i++) {
			GitChange change = changes[i];

			if (line >= change.Line + Math.Max(1, change.Count)) continue;

			if (found is not { } best || (best.Secondary && !change.Secondary)) found = change;
		}

		return found;
	}

	private static int FirstAtOrAfter(GitChange[] changes, int line) {
		int low = 0;
		int high = changes.Length;

		while (low < high) {
			int middle = (low + high) / 2;
			GitChange change = changes[middle];

			if (change.Line + Math.Max(1, change.Count) <= line) low = middle + 1;
			else high = middle;
		}

		return Math.Max(0, low - 1);
	}

	private static GitChange ToChange(LineHunk hunk, bool secondary) {
		if (hunk.ModifiedCount == 0) return new GitChange(hunk.ModifiedStart, 0, GitChangeKind.Deleted, secondary);

		return new GitChange(hunk.ModifiedStart, hunk.ModifiedCount, hunk.OriginalCount > 0 ? GitChangeKind.Modified : GitChangeKind.Added, secondary);
	}

	private static bool Overlaps(List<LineHunk> primary, LineHunk hunk) {
		int start = hunk.ModifiedStart;
		int end = hunk.ModifiedStart + hunk.ModifiedCount;

		foreach (LineHunk other in primary) {
			if (other.ModifiedStart <= end && start <= other.ModifiedStart + other.ModifiedCount) return true;
		}

		return false;
	}

	private static string? WithoutBom(string? text) => text is ['\uFEFF', ..] ? text[1..] : text;

	private static bool IsStaged(IGitRepository repository, string fullPath) {
		foreach (GitEntry entry in repository.GetStaged()) {
			if (string.Equals(Path.GetFullPath(entry.Path), fullPath, StringComparison.OrdinalIgnoreCase)) return true;
		}

		return false;
	}

	private static bool IsMerging(IGitRepository repository, string fullPath) {
		foreach (GitEntry entry in repository.GetMerge()) {
			if (string.Equals(Path.GetFullPath(entry.Path), fullPath, StringComparison.OrdinalIgnoreCase)) return true;
		}

		return false;
	}
}
