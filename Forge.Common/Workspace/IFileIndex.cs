namespace Forge.Common.Workspace;

public interface IFileIndex
{
	string GetRootPath();
	string[] GetFiles();
	bool IsComplete();

	void OnUpdated(Action handler);

	void StartScan();
	void Cancel();
}
