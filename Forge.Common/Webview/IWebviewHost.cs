using Forge.Core;

namespace Forge.Common.Webview;

public interface IWebviewHost
{
	bool IsAvailable();

	bool IsShowing(string id);

	void Show(string id, string html, string? rootDirectory, PixelRect bounds);

	void SetBounds(string id, PixelRect bounds);

	void Hide(string id);

	void HideAll();

	void ReleaseFocus();

	void SetZoom(float factor);

	void PostMessage(string id, string json);

	void OnMessage(Action<string, string> handler);

	void OnFailure(Action<string> handler);
}
