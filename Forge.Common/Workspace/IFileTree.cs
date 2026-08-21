namespace Forge.Common.Workspace;

public interface IFileTree
{
	string GetRootPath();
	FileNode GetRoot();

	void EnsureLoaded(FileNode node);
	void Toggle(FileNode node);
	void Flatten(List<FileNode> destination);
}
