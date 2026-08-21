using Forge.Common.Git;

namespace Forge.Git;

public sealed class GitRepository : IGitRepository
{
	private static readonly GitEntry[] NoEntries = [];

	private Dictionary<string, GitFileStatus> _status = [];

	private string[] _ignored = [];

	private GitEntry[] _staged = NoEntries;
	private GitEntry[] _unstaged = NoEntries;
	private GitEntry[] _merge = NoEntries;

	private readonly string _rootPath;

	private string? _branch;
	private int _ahead;
	private int _behind;

	private Action? _changed;

	private GitRepository(string root) => _rootPath = root;

	public string GetRootPath() => _rootPath;

	public string? GetBranch() => _branch;

	public int GetAhead() => _ahead;

	public int GetBehind() => _behind;

	public void OnChanged(Action handler) {
		_changed -= handler;
		_changed += handler;
	}

	public static GitRepository? Open(string workspacePath) {
		if (!Directory.Exists(workspacePath) || !GitProcess.IsAvailable) return null;

		return GitProcess.FindRepositoryRoot(workspacePath) is { } root
			? new GitRepository(root)
			: null;
	}

	public GitFileStatus StatusOf(string fullPath) {
		Dictionary<string, GitFileStatus> status = _status;
		string path = Normalise(fullPath);

		if (status.Count > 0 && status.TryGetValue(path, out GitFileStatus result)) return result;

		return IsUnderIgnoredDirectory(path) ? GitFileStatus.Ignored : GitFileStatus.Unchanged;
	}

	private bool IsUnderIgnoredDirectory(string path) {
		foreach (string directory in _ignored) {
			if (path.Length == directory.Length) {
				if (path.Equals(directory, StringComparison.OrdinalIgnoreCase)) return true;

				continue;
			}

			if (path.Length > directory.Length &&
				path[directory.Length] == Path.DirectorySeparatorChar &&
				path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) return true;
		}

		return false;
	}

	public GitFileStatus StatusOfDirectory(string directoryPath) {
		string prefix = Normalise(directoryPath);

		if (IsUnderIgnoredDirectory(prefix)) return GitFileStatus.Ignored;

		GitFileStatus best = GitFileStatus.Unchanged;

		foreach ((string path, GitFileStatus status) in _status) {
			if (status is GitFileStatus.Ignored or GitFileStatus.Unchanged ||
				!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
				path.Length <= prefix.Length ||
				path[prefix.Length] != Path.DirectorySeparatorChar) continue;

			if (status == GitFileStatus.Conflicted) return GitFileStatus.Conflicted;

			best = status == GitFileStatus.Untracked && best != GitFileStatus.Unchanged
				? best
				: status == GitFileStatus.Untracked ? GitFileStatus.Untracked : GitFileStatus.Modified;
		}

		return best;
	}

	public void Refresh() {
		ReadBranch();
		ReadStatus();
		_changed?.Invoke();
	}

	private void ReadBranch() {
		string? output = GitProcess.Run(_rootPath, "status", "--porcelain=v2", "--branch", "--null");

		if (output is null) return;

		foreach (string line in output.Split('\0')) {
			if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) {
				string head = line["# branch.head ".Length..].Trim();

				_branch = head is "(detached)" ? null : head;
			}
			else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal)) {
				string[] parts = line["# branch.ab ".Length..].Split(' ');

				if (parts.Length == 2 &&
					int.TryParse(parts[0], out int ahead) &&
					int.TryParse(parts[1], out int behind)) {
					_ahead = Math.Abs(ahead);
					_behind = Math.Abs(behind);
				}
			}
		}
	}

	public IReadOnlyList<GitEntry> GetStaged() => _staged;

	public IReadOnlyList<GitEntry> GetUnstaged() => _unstaged;

	public IReadOnlyList<GitEntry> GetMerge() => _merge;

	#region Commands

	public bool Stage(string fullPath) => GitProcess.Run(_rootPath, "add", "--", fullPath) is not null;

	public bool Unstage(string fullPath) =>
		GitProcess.Run(_rootPath, "restore", "--staged", "--", fullPath) is not null;

	public bool Discard(string fullPath, bool untracked) =>
		untracked
			? GitProcess.Run(_rootPath, "clean", "-f", "--", fullPath) is not null
			: GitProcess.Run(_rootPath, "restore", "--", fullPath) is not null;

	public bool Commit(string message) =>
		message.Trim().Length > 0 &&
		GitProcess.Run(_rootPath, "commit", "-m", message) is not null;

	#endregion

	private void ReadStatus() {
		string? output = GitProcess.Run(_rootPath,
			"status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=traditional");

		if (output is null) return;

		Dictionary<string, GitFileStatus> status = [with(StringComparer.OrdinalIgnoreCase)];
		List<string> ignored = [];
		List<GitEntry> staged = [];
		List<GitEntry> unstaged = [];
		List<GitEntry> merge = [];

		string[] records = output.Split('\0');

		for (int i = 0; i < records.Length; i++) {
			string record = records[i];

			if (record.Length < 4) continue;

			char index = record[0];
			char tree = record[1];
			string path = record[3..];

			if (index is 'R' or 'C') i++;

			GitFileStatus combined = Classify(index, tree);

			if (combined == GitFileStatus.Ignored && (path.EndsWith('/') || path.EndsWith('\\'))) {
				ignored.Add(Normalise(Path.Combine(_rootPath, path.TrimEnd('/', '\\'))));
				continue;
			}

			string full = Normalise(Path.Combine(_rootPath, path));

			status[full] = combined;

			if (combined == GitFileStatus.Conflicted) {
				merge.Add(new GitEntry(full, combined));
				continue;
			}

			if (combined == GitFileStatus.Ignored) continue;

			if (combined == GitFileStatus.Untracked) {
				unstaged.Add(new GitEntry(full, combined));
				continue;
			}

			if (index != ' ' && index != '?') staged.Add(new GitEntry(full, FromCode(index)));

			if (tree != ' ' && tree != '?') unstaged.Add(new GitEntry(full, FromCode(tree)));
		}

		_status = status;
		_ignored = ignored.Count > 0 ? [.. ignored] : [];
		_staged = staged.Count > 0 ? [.. staged] : NoEntries;
		_unstaged = unstaged.Count > 0 ? [.. unstaged] : NoEntries;
		_merge = merge.Count > 0 ? [.. merge] : NoEntries;
	}

	private static GitFileStatus FromCode(char code) => code switch {
		'M' or 'T' => GitFileStatus.Modified,
		'A' => GitFileStatus.Added,
		'D' => GitFileStatus.Deleted,
		'R' => GitFileStatus.Renamed,
		'C' => GitFileStatus.Added,
		_ => GitFileStatus.Modified,
	};

	private static GitFileStatus Classify(char index, char tree) {
		if (index == 'U' || tree == 'U' || (index == 'A' && tree == 'A') || (index == 'D' && tree == 'D')) {
			return GitFileStatus.Conflicted;
		}

		if (index == '?' || tree == '?') return GitFileStatus.Untracked;

		if (index == '!' || tree == '!') return GitFileStatus.Ignored;

		return (tree == ' ' ? index : tree) switch {
			'M' => GitFileStatus.Modified,
			'A' => GitFileStatus.Added,
			'D' => GitFileStatus.Deleted,
			'R' => GitFileStatus.Renamed,
			'C' => GitFileStatus.Added,
			'T' => GitFileStatus.Modified,
			_ => GitFileStatus.Unchanged,
		};
	}

	private static string Normalise(string path) =>
		Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
