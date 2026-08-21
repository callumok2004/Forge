namespace Forge.Common.Render;

public interface IFileIconSet
{
	bool IsReady();
	long GetMapBytes();

	void OnReady(Action handler);
}
