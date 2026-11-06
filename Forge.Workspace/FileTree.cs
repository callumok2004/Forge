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

	private const string GroupPrefix = "\u0000group/";

	private readonly string _rootPath;
	private readonly FileNode _root;

	private IReadOnlyList<ExplorerGroup>? _groups;
	private List<FileNode>? _groupNodes;
	private HashSet<FileNode>? _claimed;

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

	public void CollapseAll() {
		if (_root.Children is not null) Collapse(_root.Children);

		if (_groupNodes is not null) Collapse(_groupNodes);
	}

	private static void Collapse(List<FileNode> nodes) {
		foreach (FileNode node in nodes) {
			if (!node.IsDirectory) continue;

			node.IsExpanded = false;

			if (node.Children is not null) Collapse(node.Children);
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

		if (_groupNodes is null || _claimed is null) {
			Append(_root.Children, destination);
			return;
		}

		Append(_groupNodes, destination);

		foreach (FileNode child in _root.Children) {
			if (_claimed.Contains(child)) continue;

			destination.Add(child);

			if (child.IsDirectory && child.IsExpanded) {
				Load(child);

				if (child.Children is { Count: > 0 }) Append(child.Children, destination);
			}
		}
	}

	public void SetGroups(IReadOnlyList<ExplorerGroup>? groups) {
		_groups = groups is { Count: > 0 } ? groups : null;
		BuildGroups();
	}

	private void BuildGroups() {
		HashSet<string> expanded = new(StringComparer.Ordinal);

		if (_groupNodes is not null) CaptureGroupExpanded(_groupNodes, expanded);

		if (_claimed is not null) {
			foreach (FileNode node in _claimed) SetDepth(node, 0);
		}

		_groupNodes = null;
		_claimed = null;

		if (_groups is null || _root.Children is null) return;

		Dictionary<string, FileNode> children = new(_root.Children.Count, StringComparer.OrdinalIgnoreCase);

		foreach (FileNode child in _root.Children) children[child.FullPath] = child;

		HashSet<FileNode> claimed = [];
		List<FileNode> nodes = new(_groups.Count);

		for (int i = 0; i < _groups.Count; i++) nodes.Add(BuildGroup(_groups[i], i.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, children, claimed, expanded));

		_groupNodes = nodes;
		_claimed = claimed;
	}

	private FileNode BuildGroup(ExplorerGroup group, string key, int depth, Dictionary<string, FileNode> children, HashSet<FileNode> claimed, HashSet<string> expanded) {
		string path = GroupPrefix + key;
		FileNode node = new(path, group.Label, isDirectory: true, depth) {
			IsGroup = true,
			IsExpanded = expanded.Contains(path),
			Children = [],
		};

		for (int i = 0; i < group.Children.Count; i++) node.Children.Add(BuildGroup(group.Children[i], $"{key}.{i}", depth + 1, children, claimed, expanded));

		foreach (string resource in group.Resources) {
			if (ResolveResource(Path.GetFullPath(resource).TrimEnd(Path.DirectorySeparatorChar), children, claimed) is not { } resolved) continue;

			SetDepth(resolved, depth + 1);
			node.Children.Add(resolved);
		}

		return node;
	}

	private FileNode? ResolveResource(string path, Dictionary<string, FileNode> children, HashSet<FileNode> claimed) {
		if (children.TryGetValue(path, out FileNode? direct)) {
			claimed.Add(direct);
			return direct;
		}

		foreach (FileNode child in _root.Children!) {
			string prefix = child.FullPath.TrimEnd(Path.DirectorySeparatorChar);

			if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path[prefix.Length] == Path.DirectorySeparatorChar) {
				claimed.Add(child);
				break;
			}
		}

		bool directory = Directory.Exists(path);

		if (!directory && !File.Exists(path)) return null;

		return new FileNode(path, Path.GetFileName(path), directory, 0);
	}

	private static void CaptureGroupExpanded(List<FileNode> nodes, HashSet<string> destination) {
		foreach (FileNode node in nodes) {
			if (!node.IsGroup) continue;

			if (node.IsExpanded) destination.Add(node.FullPath);

			if (node.Children is not null) CaptureGroupExpanded(node.Children, destination);
		}
	}

	private static void SetDepth(FileNode node, int depth) {
		node.Depth = depth;

		if (node.Children is null) return;

		foreach (FileNode child in node.Children) SetDepth(child, depth + 1);
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

		if (ReferenceEquals(node, _root) && _groups is not null) BuildGroups();

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
		if (!node.IsDirectory || node.IsGroup || node.Children is not null) return;

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
