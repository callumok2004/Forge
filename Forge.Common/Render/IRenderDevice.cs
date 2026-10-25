using Forge.Theming;

namespace Forge.Common.Render;

public interface IRenderDevice
{
	int GetWidth();
	int GetHeight();
	float GetDpi();

	string? GetLastFailure();

	void BeginDraw();
	bool BeginPass(int left, int top, int right, int bottom);
	void EndPass();
	void EndDraw();

	void Clear(Color color);
	void Resize(int width, int height, float dpi);
}
