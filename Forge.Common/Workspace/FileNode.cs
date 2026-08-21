namespace Forge.Common.Workspace;

public sealed class FileNode(string fullPath, string name, bool isDirectory, int depth)
{
	public string FullPath { get; } = fullPath;

	public string Name { get; } = name;

	public bool IsDirectory { get; } = isDirectory;

	public int Depth { get; } = depth;

	public bool IsExpanded { get; set; }

	public List<FileNode>? Children { get; set; }
}
