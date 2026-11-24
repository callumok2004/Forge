namespace Forge.Common.Git;

public interface IGitRepositories
{
	IReadOnlyList<IGitRepository> GetAll();

	IGitRepository? For(string path);

	void OnChanged(Action handler);

	void Close(IGitRepository repository);

	void RefreshFor(IReadOnlyCollection<string> paths);
}
