using System.Collections.Concurrent;

namespace Forge.Workspace;

public sealed class WorkspaceWatcher : IDisposable
{
	private const int SettleMilliseconds = 150;

	private readonly FileSystemWatcher _watcher;
	private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.OrdinalIgnoreCase);
	private readonly Timer _settle;
	private readonly ConcurrentDictionary<string, byte> _gitPaths = new(StringComparer.OrdinalIgnoreCase);
	private readonly Action<IReadOnlyCollection<string>, IReadOnlyCollection<string>> _changed;
	private readonly string _root;

	public WorkspaceWatcher(string root, Action<IReadOnlyCollection<string>, IReadOnlyCollection<string>> changed) {
		_root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
		_changed = changed;
		_settle = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

		_watcher = new FileSystemWatcher(_root) {
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
			InternalBufferSize = 64 * 1024,
		};

		_watcher.Created += OnChanged;
		_watcher.Deleted += OnChanged;
		_watcher.Changed += OnChanged;
		_watcher.Renamed += (_, args) => {
			Note(args.OldFullPath);
			Note(args.FullPath);
		};

		_watcher.EnableRaisingEvents = true;
	}

	private void OnChanged(object sender, FileSystemEventArgs args) => Note(args.FullPath);

	private void Note(string path) {
		ReadOnlySpan<char> relative = path.AsSpan(Math.Min(path.Length, _root.Length)).TrimStart(Path.DirectorySeparatorChar);

		if (GitInternal(relative, out ReadOnlySpan<char> inside)) {
			if (!IsGitStateChange(inside)) return;

			_gitPaths.TryAdd(path, 0);
			_settle.Change(SettleMilliseconds, Timeout.Infinite);
			return;
		}

		if (IsIgnored(relative)) return;

		if (Path.GetDirectoryName(path) is { } directory) _directories.TryAdd(directory, 0);

		_gitPaths.TryAdd(path, 0);
		_settle.Change(SettleMilliseconds, Timeout.Infinite);
	}

	private static bool GitInternal(ReadOnlySpan<char> relative, out ReadOnlySpan<char> inside) {
		foreach (Range segment in relative.Split(Path.DirectorySeparatorChar)) {
			if (!relative[segment].Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;

			int end = segment.End.GetOffset(relative.Length);
			inside = end >= relative.Length ? default : relative[(end + 1)..];
			return true;
		}

		inside = default;
		return false;
	}

	public static bool IsGitStateChange(ReadOnlySpan<char> path) {
		while (path.StartsWith("modules" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
			int state = StateStart(path[8..]);

			if (state < 0) return false;

			path = path[(8 + state)..];
		}

		if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;

		return path.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || path.Equals("index", StringComparison.OrdinalIgnoreCase) || path.Equals("packed-refs", StringComparison.OrdinalIgnoreCase) || path.EndsWith("_HEAD", StringComparison.OrdinalIgnoreCase) || path.StartsWith("refs", StringComparison.OrdinalIgnoreCase);
	}

	private static int StateStart(ReadOnlySpan<char> path) {
		int offset = 0;

		while (offset < path.Length) {
			int separator = path[offset..].IndexOf(Path.DirectorySeparatorChar);
			ReadOnlySpan<char> segment = separator < 0 ? path[offset..] : path.Slice(offset, separator);

			if (offset > 0 && (segment.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || segment.Equals("index", StringComparison.OrdinalIgnoreCase) || segment.Equals("packed-refs", StringComparison.OrdinalIgnoreCase) || segment.EndsWith("_HEAD", StringComparison.OrdinalIgnoreCase) || segment.Equals("refs", StringComparison.OrdinalIgnoreCase) || segment.Equals("modules", StringComparison.OrdinalIgnoreCase) || segment.Equals("objects", StringComparison.OrdinalIgnoreCase) || segment.Equals("logs", StringComparison.OrdinalIgnoreCase))) return offset;

			if (separator < 0) return -1;

			offset += separator + 1;
		}

		return -1;
	}

	private static bool IsIgnored(ReadOnlySpan<char> relative) {
		foreach (Range segment in relative.Split(Path.DirectorySeparatorChar))
			if (FileTree.IsIgnored(relative[segment])) return true;

		return false;
	}

	private void Flush() {
		string[] directories = [.. _directories.Keys];
		_directories.Clear();

		string[] git = [.. _gitPaths.Keys];
		_gitPaths.Clear();

		if (directories.Length > 0 || git.Length > 0) _changed(directories, git);
	}

	public void Dispose() {
		_watcher.Dispose();
		_settle.Dispose();
	}
}
