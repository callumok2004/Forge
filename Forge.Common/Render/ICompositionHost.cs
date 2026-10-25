namespace Forge.Common.Render;

public interface ICompositionHost
{
	nint CreateVisual();

	void DestroyVisual(nint visual);

	void SetVisualBounds(nint visual, int left, int top, int width, int height);

	void Commit();
}
