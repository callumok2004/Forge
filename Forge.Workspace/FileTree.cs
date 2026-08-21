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

	public void Flatten(List<FileNode> destination) {
		destination.Clear();

		if (_root.Children is null) return;

		Append(_root.Children, destination);
	}

	public static bool IsHidden(string directoryName) => Array.IndexOf(HiddenDirectories, directoryName) >= 0;

	public static bool IsIgnored(string directoryName) => IsHidden(directoryName) || Array.IndexOf(UnindexedDirectories, directoryName) >= 0;

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
