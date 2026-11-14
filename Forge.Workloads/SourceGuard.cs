namespace Forge.Workloads;

internal static class SourceGuard
{
	private static readonly HashSet<string> Ignored = [with(StringComparer.OrdinalIgnoreCase), "bin", "obj", ".git", ".vs", "node_modules", "TestResults"];

	public static Dictionary<string, (long Length, DateTime Written)> Capture(string root) {
		Dictionary<string, (long, DateTime)> files = [with(StringComparer.OrdinalIgnoreCase)];
		Walk(new DirectoryInfo(root), files);
		return files;
	}

	private static void Walk(DirectoryInfo directory, Dictionary<string, (long, DateTime)> files) {
		IEnumerable<FileSystemInfo> entries;

		try {
			entries = directory.EnumerateFileSystemInfos();
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) {
			return;
		}

		foreach (FileSystemInfo entry in entries) {
			if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

			if (entry is FileInfo file) files[file.FullName] = (file.Length, file.LastWriteTimeUtc);
			else if (entry is DirectoryInfo child && !Ignored.Contains(child.Name)) Walk(child, files);
		}
	}

	public static HashSet<string> CaptureIntermediate(string root) {
		HashSet<string> files = [with(StringComparer.OrdinalIgnoreCase)];

		foreach (string obj in Directory.EnumerateDirectories(root, "obj", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })) {
			foreach (string file in Directory.EnumerateFiles(obj, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })) files.Add(file);
		}

		return files;
	}

	public static List<string> RemoveCreated(HashSet<string> before, HashSet<string> after) {
		List<string> removed = [];

		foreach (string file in after) {
			if (before.Contains(file)) continue;

			try {
				File.Delete(file);
				removed.Add(file);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			}
		}

		return removed;
	}

	public static List<string> Changes(Dictionary<string, (long Length, DateTime Written)> before, Dictionary<string, (long Length, DateTime Written)> after) {
		List<string> changes = [];

		foreach ((string path, (long, DateTime) state) in after) {
			if (!before.TryGetValue(path, out (long, DateTime) previous)) changes.Add($"added {path}");
			else if (previous != state) changes.Add($"modified {path}");
		}

		foreach (string path in before.Keys) {
			if (!after.ContainsKey(path)) changes.Add($"deleted {path}");
		}

		return changes;
	}
}
