using System.Collections.Concurrent;

namespace Forge.Workspace;

public sealed class WorkspaceWatcher : IDisposable
{
	private const int SettleMilliseconds = 150;

	private readonly FileSystemWatcher _watcher;
	private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.OrdinalIgnoreCase);
	private readonly Timer _settle;
	private readonly Action<IReadOnlyCollection<string>, bool> _changed;
	private readonly string _root;

	private volatile bool _gitChanged;

	public WorkspaceWatcher(string root, Action<IReadOnlyCollection<string>, bool> changed) {
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
		int separator = relative.IndexOf(Path.DirectorySeparatorChar);
		ReadOnlySpan<char> top = separator < 0 ? relative : relative[..separator];

		if (top.Equals(".git", StringComparison.OrdinalIgnoreCase)) {
			if (!IsGitStateChange(separator < 0 ? default : relative[(separator + 1)..])) return;

			_gitChanged = true;
			_settle.Change(SettleMilliseconds, Timeout.Infinite);
			return;
		}

		if (IsIgnored(relative)) return;

		if (Path.GetDirectoryName(path) is { } directory) _directories.TryAdd(directory, 0);

		_gitChanged = true;
		_settle.Change(SettleMilliseconds, Timeout.Infinite);
	}

	private static bool IsGitStateChange(ReadOnlySpan<char> path) {
		if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;

		return path.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || path.Equals("index", StringComparison.OrdinalIgnoreCase) || path.Equals("packed-refs", StringComparison.OrdinalIgnoreCase) || path.EndsWith("_HEAD", StringComparison.OrdinalIgnoreCase) || path.StartsWith("refs", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsIgnored(ReadOnlySpan<char> relative) {
		foreach (Range segment in relative.Split(Path.DirectorySeparatorChar))
			if (FileTree.IsIgnored(relative[segment])) return true;

		return false;
	}

	private void Flush() {
		string[] directories = [.. _directories.Keys];
		_directories.Clear();

		bool git = _gitChanged;
		_gitChanged = false;

		if (directories.Length > 0 || git) _changed(directories, git);
	}

	public void Dispose() {
		_watcher.Dispose();
		_settle.Dispose();
	}
}
