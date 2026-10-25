using Forge.Common.Workspace;

namespace Forge.Workspace;

public sealed class FileTree : IFileTree
{
	private static readonly string[] HiddenDirectories = [".git", ".svn", ".hg", "CVS"];

	private static readonly string[] UnindexedDirectories =
	[
		".vs", ".idea", "node_modules", "bower_components", "bin", "obj", "artifacts", "packages",
		"BenchmarkDotNet.Artifacts", ".vscode-test", "dist", "target",
	];

	private readonly string _rootPath;
	private readonly FileNode _root;

	public FileTree(string rootPath) {
		_rootPath = Path.GetFullPath(rootPath);
		_root = new FileNode(_rootPath, Path.GetFileName(_rootPath.TrimEnd(Path.DirectorySeparatorChar)), isDirectory: true, depth: -1) {
			IsExpanded = true,
		};

		Load(_root);
	}

	public string GetRootPath() => _rootPath;

	public FileNode GetRoot() => _root;

	public void EnsureLoaded(FileNode node) => Load(node);

	public void Toggle(FileNode node) {
		if (!node.IsDirectory) return;

		node.IsExpanded = !node.IsExpanded;
		if (node.IsExpanded) Load(node);
	}

	public void CaptureExpanded(List<string> destination) {
		destination.Clear();

		if (_root.Children is not null) CaptureExpanded(_root.Children, destination);
	}

	private static void CaptureExpanded(List<FileNode> nodes, List<string> destination) {
		foreach (FileNode node in nodes) {
			if (!node.IsDirectory || !node.IsExpanded) continue;

			destination.Add(node.FullPath);

			if (node.Children is not null) CaptureExpanded(node.Children, destination);
		}
	}

	public void RestoreExpanded(IReadOnlyList<string> paths) {
		HashSet<string> wanted = new(paths, StringComparer.OrdinalIgnoreCase);

		if (wanted.Count > 0 && _root.Children is not null) RestoreExpanded(_root.Children, wanted);
	}

	private static void RestoreExpanded(List<FileNode> nodes, HashSet<string> wanted) {
		foreach (FileNode node in nodes) {
			if (!node.IsDirectory || !wanted.Contains(node.FullPath)) continue;

			node.IsExpanded = true;
			Load(node);

			if (node.Children is not null) RestoreExpanded(node.Children, wanted);
		}
	}

	public void Flatten(List<FileNode> destination) {
		destination.Clear();

		if (_root.Children is null) return;

		Append(_root.Children, destination);
	}

	public bool Refresh(string directoryPath) {
		if (Find(_root, Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar)) is not { Children: { } existing } node) return false;

		Dictionary<string, FileNode> previous = new(existing.Count, StringComparer.OrdinalIgnoreCase);

		foreach (FileNode child in existing) previous[child.FullPath] = child;

		node.Children = null;
		Load(node);

		List<FileNode> reloaded = node.Children!;

		for (int i = 0; i < reloaded.Count; i++)
			if (previous.TryGetValue(reloaded[i].FullPath, out FileNode? kept) && kept.IsDirectory == reloaded[i].IsDirectory) reloaded[i] = kept;

		return true;
	}

	private static FileNode? Find(FileNode node, string path) {
		if (string.Equals(node.FullPath.TrimEnd(Path.DirectorySeparatorChar), path, StringComparison.OrdinalIgnoreCase)) return node;

		if (node.Children is null || !path.StartsWith(node.FullPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;

		foreach (FileNode child in node.Children)
			if (child.IsDirectory && Find(child, path) is { } found) return found;

		return null;
	}

	public static bool IsHidden(string directoryName) => Array.IndexOf(HiddenDirectories, directoryName) >= 0;

	public static bool IsIgnored(string directoryName) => IsHidden(directoryName) || Array.IndexOf(UnindexedDirectories, directoryName) >= 0;

	public static bool IsIgnored(ReadOnlySpan<char> directoryName) => Contains(HiddenDirectories, directoryName) || Contains(UnindexedDirectories, directoryName);

	private static bool Contains(string[] names, ReadOnlySpan<char> name) {
		foreach (string candidate in names)
			if (name.Equals(candidate, StringComparison.Ordinal)) return true;

		return false;
	}

	private static void Load(FileNode node) {
		if (!node.IsDirectory || node.Children is not null) return;

		List<FileNode> children = [];

		try {
			foreach (string directory in Directory.EnumerateDirectories(node.FullPath)) {
				string name = Path.GetFileName(directory);
				if (!IsHidden(name)) children.Add(new FileNode(directory, name, isDirectory: true, node.Depth + 1));
			}

			foreach (string file in Directory.EnumerateFiles(node.FullPath)) {
				children.Add(new FileNode(file, Path.GetFileName(file), isDirectory: false, node.Depth + 1));
			}
		}
		catch (UnauthorizedAccessException) {
		}
		catch (IOException) {
		}

		children.Sort(static (left, right) => {
			if (left.IsDirectory != right.IsDirectory) return left.IsDirectory ? -1 : 1;

			return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
		});

		node.Children = children;
	}

	private static void Append(List<FileNode> nodes, List<FileNode> destination) {
		foreach (FileNode node in nodes) {
			destination.Add(node);

			if (node.IsDirectory && node.IsExpanded) {
				Load(node);
				if (node.Children is { Count: > 0 }) Append(node.Children, destination);
			}
		}
	}
}
