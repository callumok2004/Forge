using Forge.Common.Git;

namespace Forge.Git;

public sealed class GitRepository : IGitRepository
{
	private static readonly GitEntry[] NoEntries = [];

	private readonly string _rootPath;

	private Snapshot _snapshot = Snapshot.Empty;
	private string? _lastStatus;
	private string? _lastIgnored;

	private int _pending;
	private int _running;

	private Action? _changed;

	private GitRepository(string root) => _rootPath = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

	public string GetRootPath() => _rootPath;

	public string? GetBranch() => _snapshot.Branch;

	public int GetAhead() => _snapshot.Ahead;

	public int GetBehind() => _snapshot.Behind;

	public IReadOnlyList<GitEntry> GetStaged() => _snapshot.Staged;

	public IReadOnlyList<GitEntry> GetUnstaged() => _snapshot.Unstaged;

	public IReadOnlyList<GitEntry> GetMerge() => _snapshot.Merge;

	public void OnChanged(Action handler) {
		_changed -= handler;
		_changed += handler;
	}

	public static GitRepository? Open(string workspacePath) {
		if (!Directory.Exists(workspacePath) || !GitProcess.IsAvailable) return null;

		return GitProcess.FindRepositoryRoot(workspacePath) is { } root ? new GitRepository(root) : null;
	}

	public GitFileStatus StatusOf(string fullPath) {
		Snapshot snapshot = _snapshot;
		ReadOnlySpan<char> path = Trim(fullPath);

		if (snapshot.Files.TryGetValue(path, out GitFileStatus status)) return status;

		return snapshot.IsIgnored(path) ? GitFileStatus.Ignored : GitFileStatus.Unchanged;
	}

	public GitFileStatus StatusOfDirectory(string directoryPath) {
		Snapshot snapshot = _snapshot;
		ReadOnlySpan<char> path = Trim(directoryPath);

		if (snapshot.IsIgnored(path)) return GitFileStatus.Ignored;

		return snapshot.Directories.TryGetValue(path, out GitFileStatus status) ? status : GitFileStatus.Unchanged;
	}

	public void Refresh() {
		if (Interlocked.Exchange(ref _pending, 1) == 1) return;

		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

		ThreadPool.UnsafeQueueUserWorkItem(static state => ((GitRepository)state!).Drain(), this);
	}

	private void Drain() {
		do {
			while (Interlocked.Exchange(ref _pending, 0) == 1) Load();

			Volatile.Write(ref _running, 0);
		}
		while (Volatile.Read(ref _pending) == 1 && Interlocked.CompareExchange(ref _running, 1, 0) == 0);
	}

	private void Load() {
		string? status = GitProcess.Run(_rootPath, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all");

		if (status is null) return;

		string ignored = GitProcess.Run(_rootPath, "ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory", "--no-empty-directory") ?? string.Empty;

		if (status == _lastStatus && ignored == _lastIgnored) return;

		_lastStatus = status;
		_lastIgnored = ignored;
		_snapshot = Parse(status, ignored);
		_changed?.Invoke();
	}

	#region Commands

	public bool Stage(string fullPath) => Done(GitProcess.Run(_rootPath, "add", "--", fullPath));

	public bool Unstage(string fullPath) => Done(GitProcess.Run(_rootPath, "restore", "--staged", "--", fullPath));

	public bool Discard(string fullPath, bool untracked) => Done(untracked ? GitProcess.Run(_rootPath, "clean", "-f", "--", fullPath) : GitProcess.Run(_rootPath, "restore", "--", fullPath));

	public bool Commit(string message) => message.Trim().Length > 0 && Done(GitProcess.Run(_rootPath, "commit", "-m", message));

	private bool Done(string? output) {
		if (output is null) return false;

		Refresh();
		return true;
	}

	#endregion

	private Snapshot Parse(string status, string ignored) {
		Dictionary<string, GitFileStatus> files = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, GitFileStatus> directories = new(StringComparer.OrdinalIgnoreCase);
		List<string> ignoredDirectories = [];
		List<GitEntry> staged = [];
		List<GitEntry> unstaged = [];
		List<GitEntry> merge = [];

		string? branch = null;
		int ahead = 0;
		int behind = 0;

		ReadOnlySpan<char> remaining = status;

		while (Next(ref remaining, out ReadOnlySpan<char> record)) {
			if (record.Length < 3) continue;

			switch (record[0]) {
				case '#':
					ReadHeader(record, ref branch, ref ahead, ref behind);
					continue;

				case '?': {
						string full = FullPath(record[2..]);
						files[full] = GitFileStatus.Untracked;
						unstaged.Add(new GitEntry(full, GitFileStatus.Untracked));
						Propagate(directories, full, GitFileStatus.Untracked);
						continue;
					}

				case '1' or '2' or 'u': {
						char index = Code(record[2]);
						char tree = Code(record[3]);
						int fields = record[0] switch { '1' => 8, '2' => 9, _ => 10 };

						if (record[0] == '2') Next(ref remaining, out _);

						if (FieldAfter(record, fields) is not { IsEmpty: false } relative) continue;

						string full = FullPath(relative);
						GitFileStatus combined = Classify(index, tree);

						files[full] = combined;
						Propagate(directories, full, combined);

						if (combined == GitFileStatus.Conflicted) {
							merge.Add(new GitEntry(full, combined));
							continue;
						}

						if (index != ' ') staged.Add(new GitEntry(full, FromCode(index)));

						if (tree != ' ') unstaged.Add(new GitEntry(full, FromCode(tree)));

						continue;
					}
			}
		}

		remaining = ignored;

		while (Next(ref remaining, out ReadOnlySpan<char> record)) {
			if (record.IsEmpty) continue;

			if (record[^1] is '/' or '\\') ignoredDirectories.Add(FullPath(record[..^1]));
			else files.TryAdd(FullPath(record), GitFileStatus.Ignored);
		}

		return new Snapshot(files, directories, [.. ignoredDirectories], branch, ahead, behind, staged.Count > 0 ? [.. staged] : NoEntries, unstaged.Count > 0 ? [.. unstaged] : NoEntries, merge.Count > 0 ? [.. merge] : NoEntries);
	}

	private static void ReadHeader(ReadOnlySpan<char> record, ref string? branch, ref int ahead, ref int behind) {
		const string Head = "# branch.head ";
		const string Counts = "# branch.ab ";

		if (record.StartsWith(Head, StringComparison.Ordinal)) {
			ReadOnlySpan<char> head = record[Head.Length..].Trim();
			branch = head.SequenceEqual("(detached)") ? null : head.ToString();
			return;
		}

		if (!record.StartsWith(Counts, StringComparison.Ordinal)) return;

		ReadOnlySpan<char> counts = record[Counts.Length..];
		int space = counts.IndexOf(' ');

		if (space < 0 || !int.TryParse(counts[..space], out int a) || !int.TryParse(counts[(space + 1)..], out int b)) return;

		ahead = Math.Abs(a);
		behind = Math.Abs(b);
	}

	private void Propagate(Dictionary<string, GitFileStatus> directories, string full, GitFileStatus status) {
		if (status is GitFileStatus.Ignored or GitFileStatus.Unchanged) return;

		GitFileStatus rolled = status switch {
			GitFileStatus.Conflicted => GitFileStatus.Conflicted,
			GitFileStatus.Untracked => GitFileStatus.Untracked,
			_ => GitFileStatus.Modified,
		};

		Dictionary<string, GitFileStatus>.AlternateLookup<ReadOnlySpan<char>> lookup = directories.GetAlternateLookup<ReadOnlySpan<char>>();
		ReadOnlySpan<char> path = full;

		while (path.LastIndexOf(Path.DirectorySeparatorChar) is var separator && separator > _rootPath.Length) {
			path = path[..separator];

			if (lookup.TryGetValue(path, out GitFileStatus existing)) {
				GitFileStatus merged = Merge(existing, rolled);

				if (merged == existing) return;

				lookup[path] = merged;
				continue;
			}

			lookup[path] = rolled;
		}
	}

	private static GitFileStatus Merge(GitFileStatus existing, GitFileStatus incoming) {
		if (existing == GitFileStatus.Conflicted || incoming == GitFileStatus.Conflicted) return GitFileStatus.Conflicted;

		return existing == GitFileStatus.Modified || incoming == GitFileStatus.Modified ? GitFileStatus.Modified : GitFileStatus.Untracked;
	}

	private string FullPath(ReadOnlySpan<char> relative) {
		int length = _rootPath.Length + 1 + relative.Length;
		Span<char> buffer = length <= 1024 ? stackalloc char[length] : new char[length];

		_rootPath.CopyTo(buffer);
		buffer[_rootPath.Length] = Path.DirectorySeparatorChar;

		Span<char> tail = buffer[(_rootPath.Length + 1)..];
		relative.CopyTo(tail);
		tail.Replace('/', Path.DirectorySeparatorChar);

		return new string(buffer);
	}

	private static bool Next(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> record) {
		if (remaining.IsEmpty) {
			record = default;
			return false;
		}

		int end = remaining.IndexOf('\0');

		if (end < 0) {
			record = remaining;
			remaining = default;
			return true;
		}

		record = remaining[..end];
		remaining = remaining[(end + 1)..];
		return true;
	}

	private static ReadOnlySpan<char> FieldAfter(ReadOnlySpan<char> record, int spaces) {
		int position = 0;

		for (int i = 0; i < spaces; i++) {
			int next = record[position..].IndexOf(' ');

			if (next < 0) return default;

			position += next + 1;
		}

		return record[position..];
	}

	private static ReadOnlySpan<char> Trim(string path) => path.AsSpan().TrimEnd(Path.DirectorySeparatorChar);

	private static char Code(char code) => code == '.' ? ' ' : code;

	private static GitFileStatus FromCode(char code) => code switch {
		'A' or 'C' => GitFileStatus.Added,
		'D' => GitFileStatus.Deleted,
		'R' => GitFileStatus.Renamed,
		_ => GitFileStatus.Modified,
	};

	private static GitFileStatus Classify(char index, char tree) {
		if (index == 'U' || tree == 'U' || (index == 'A' && tree == 'A') || (index == 'D' && tree == 'D')) return GitFileStatus.Conflicted;

		return (tree == ' ' ? index : tree) switch {
			'M' or 'T' => GitFileStatus.Modified,
			'A' or 'C' => GitFileStatus.Added,
			'D' => GitFileStatus.Deleted,
			'R' => GitFileStatus.Renamed,
			_ => GitFileStatus.Unchanged,
		};
	}

	private sealed class Snapshot(Dictionary<string, GitFileStatus> files, Dictionary<string, GitFileStatus> directories, string[] ignored, string? branch, int ahead, int behind, GitEntry[] staged, GitEntry[] unstaged, GitEntry[] merge)
	{
		public static readonly Snapshot Empty = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), [], null, 0, 0, NoEntries, NoEntries, NoEntries);

		public Dictionary<string, GitFileStatus>.AlternateLookup<ReadOnlySpan<char>> Files { get; } = files.GetAlternateLookup<ReadOnlySpan<char>>();

		public Dictionary<string, GitFileStatus>.AlternateLookup<ReadOnlySpan<char>> Directories { get; } = directories.GetAlternateLookup<ReadOnlySpan<char>>();

		public string? Branch { get; } = branch;

		public int Ahead { get; } = ahead;

		public int Behind { get; } = behind;

		public GitEntry[] Staged { get; } = staged;

		public GitEntry[] Unstaged { get; } = unstaged;

		public GitEntry[] Merge { get; } = merge;

		public bool IsIgnored(ReadOnlySpan<char> path) {
			foreach (string directory in ignored) {
				if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) continue;

				if (path.Length == directory.Length || path[directory.Length] == Path.DirectorySeparatorChar) return true;
			}

			return false;
		}
	}
}
