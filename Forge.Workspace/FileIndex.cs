using System.Collections.Concurrent;

using Forge.Common.Workspace;

namespace Forge.Workspace;

public sealed class FileIndex(string rootPath) : IFileIndex
{
	private readonly string _rootPath = Path.GetFullPath(rootPath);
	private readonly CancellationTokenSource _cancellation = new();

	private volatile string[] _files = [];
	private volatile bool _complete;

	private Action? _updated;

	public string GetRootPath() => _rootPath;

	public string[] GetFiles() => _files;

	public bool IsComplete() => _complete;

	public void OnUpdated(Action handler) {
		_updated -= handler;
		_updated += handler;
	}

	public void StartScan() => Task.Run(Scan, _cancellation.Token);

	public void Cancel() => _cancellation.Cancel();

	private void Scan() {
		List<string> found = [];
		ConcurrentQueue<string> pending = new();
		pending.Enqueue(_rootPath);

		int sincePublish = 0;

		while (pending.TryDequeue(out string? directory)) {
			if (_cancellation.IsCancellationRequested) return;

			try {
				foreach (string child in Directory.EnumerateDirectories(directory)) {
					if (!FileTree.IsIgnored(Path.GetFileName(child))) pending.Enqueue(child);
				}

				foreach (string file in Directory.EnumerateFiles(directory)) {
					found.Add(Path.GetRelativePath(_rootPath, file));
					sincePublish++;
				}
			}
			catch (UnauthorizedAccessException) {
				continue;
			}
			catch (IOException) {
				continue;
			}

			if (sincePublish >= 2000) {
				sincePublish = 0;
				Publish(found);
			}
		}

		Publish(found);
		_complete = true;
		_updated?.Invoke();
	}

	private void Publish(List<string> found) {
		_files = [.. found];
		_updated?.Invoke();
	}
}
