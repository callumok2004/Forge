namespace Forge.Common.Git;

public interface IFileDiff
{
	string? GetPath();
	IReadOnlyList<GitChange> GetChanges();

	GitChange? At(int line);
	void Sync(int version, Func<string> text);
	void Clear();
}
