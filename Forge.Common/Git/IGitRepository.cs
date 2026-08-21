namespace Forge.Common.Git;

public interface IGitRepository
{
	string GetRootPath();
	string? GetBranch();

	int GetAhead();
	int GetBehind();

	void OnChanged(Action handler);

	GitFileStatus StatusOf(string fullPath);
	GitFileStatus StatusOfDirectory(string directoryPath);

	IReadOnlyList<GitEntry> GetStaged();
	IReadOnlyList<GitEntry> GetUnstaged();
	IReadOnlyList<GitEntry> GetMerge();

	void Refresh();

	bool Stage(string fullPath);
	bool Unstage(string fullPath);
	bool Discard(string fullPath, bool untracked);
	bool Commit(string message);
}
