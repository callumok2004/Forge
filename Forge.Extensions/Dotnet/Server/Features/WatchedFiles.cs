using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class WatchedFiles(RoslynLanguageClient client) : IDisposable
{
	private const int Created = 1;
	private const int Changed = 2;
	private const int Deleted = 3;
	private const int CoalesceMilliseconds = 150;
	private const int ReloadMilliseconds = 500;

	private static readonly string[] Extensions = [".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".slnf", ".editorconfig"];

	private readonly Dictionary<string, int> _pending = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	private FileSystemWatcher? _watcher;
	private Timer? _flush;
	private Timer? _reload;

	public event Action? ProjectsReopened;

	public void Watch(string? directory) {
		Dispose();

		if (directory is null || !Directory.Exists(directory)) return;

		_flush = new Timer(_ => Flush());
		_reload = new Timer(_ => Reopen());
		_watcher = new FileSystemWatcher(directory) {
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
			InternalBufferSize = 64 * 1024,
		};

		_watcher.Created += (_, change) => Queue(change.FullPath, Created);
		_watcher.Changed += (_, change) => Queue(change.FullPath, Changed);
		_watcher.Deleted += (_, change) => Queue(change.FullPath, Deleted);
		_watcher.Renamed += (_, change) => {
			Queue(change.OldFullPath, Deleted);
			Queue(change.FullPath, Created);
		};

		_watcher.EnableRaisingEvents = true;
	}

	private static bool Relevant(string path) {
		if (path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase)) return false;

		foreach (string extension in Extensions) {
			if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
		}

		return false;
	}

	private void Queue(string path, int kind) {
		if (!Relevant(path)) return;

		lock (_sync) _pending[path] = _pending.TryGetValue(path, out int previous) && previous == Created && kind == Changed ? Created : kind;

		_flush?.Change(CoalesceMilliseconds, Timeout.Infinite);

		if (kind != Changed && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) _reload?.Change(ReloadMilliseconds, Timeout.Infinite);
	}

	private void Flush() {
		JsonArray changes = [];

		lock (_sync) {
			foreach ((string path, int kind) in _pending) changes.Add(new JsonObject { ["uri"] = RoslynLanguageClient.ToUri(path), ["type"] = kind });

			_pending.Clear();
		}

		if (changes.Count > 0) client.Notify("workspace/didChangeWatchedFiles", new JsonObject { ["changes"] = changes });
	}

	private void Reopen() {
		if (!client.OpenWorkspace()) return;

		client.Contexts.Invalidate();
		ProjectsReopened?.Invoke();
	}

	public void Dispose() {
		_watcher?.Dispose();
		_watcher = null;
		_flush?.Dispose();
		_flush = null;
		_reload?.Dispose();
		_reload = null;
	}
}
