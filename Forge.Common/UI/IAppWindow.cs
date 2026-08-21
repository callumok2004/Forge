using Forge.Core;

namespace Forge.Common.UI;

public interface IAppWindow
{
	nint GetHandle();

	uint GetDpi();
	float GetDpiScale();

	int GetClientWidth();
	int GetClientHeight();

	bool IsMaximized();

	void Show();
	void Minimize();
	void ToggleMaximize();
	void CloseWindow();

	void SetTitle(string title);

	void Invalidate();
	void Invalidate(PixelRect area);

	void StartTimer(nuint id, uint intervalMs);
	void StopTimer(nuint id);
}
