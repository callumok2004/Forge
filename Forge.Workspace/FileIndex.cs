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

	private static readonly EnumerationOptions ScanOptions = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };

	private void Scan() {
		List<string> found = [];
		List<string> directories = [];
		Queue<string> pending = new();
		pending.Enqueue(_rootPath);

		int rootLength = _rootPath.TrimEnd(Path.DirectorySeparatorChar).Length + 1;
		int publishAt = 2000;

		while (pending.TryDequeue(out string? directory)) {
			if (_cancellation.IsCancellationRequested) return;

			directories.Clear();

			try {
				System.IO.Enumeration.FileSystemEnumerable<string?> entries = new(directory, (ref System.IO.Enumeration.FileSystemEntry entry) => {
					if (entry.IsDirectory) {
						if (!FileTree.IsIgnored(entry.FileName)) directories.Add(entry.ToFullPath());

						return null;
					}

					return string.Concat(entry.Directory[Math.Min(rootLength, entry.Directory.Length)..], entry.Directory.Length > rootLength ? "\\" : string.Empty, entry.FileName);
				}, ScanOptions);

				foreach (string? relative in entries) {
					if (relative is not null) found.Add(relative);
				}
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) {
				continue;
			}

			foreach (string child in directories) pending.Enqueue(child);

			if (found.Count >= publishAt) {
				publishAt = found.Count * 2;
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
