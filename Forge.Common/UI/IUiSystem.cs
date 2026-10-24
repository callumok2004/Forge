using Forge.Common.Render;
using Forge.Core;

namespace Forge.Common.UI;

public interface IUiSystem
{
	float GetScale();
	float GetViewportWidth();
	float GetViewportHeight();

	void Repaint(PixelRect area);
	void InvalidateLayout(Panel panel);
	void LayoutPerformed(Panel panel);

	Panel? GetFocus();
	void SetFocus(Panel? panel);
	bool HasContext(string key);

	Panel? GetMouseOver();
	Panel? GetMouseCapture();
	void SetMouseCapture(Panel? panel);
	KeyModifiers GetModifiers();

	void AddPopup(Panel popup, bool modal);
	void RemovePopup(Panel popup);

	void SetThinking(Panel panel, bool thinking);
	void AddTickSignal(Panel panel, int intervalMilliseconds);
	void RemoveTickSignal(Panel panel);

	void PostCommand(string command, Panel? from);
	string? GetKeybinding(string command);
	bool IsCommandEnabled(string command);

	IFontSet GetFonts();
}
