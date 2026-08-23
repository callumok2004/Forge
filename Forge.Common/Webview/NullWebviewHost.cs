using Forge.Core;

namespace Forge.Common.Webview;

public sealed class NullWebviewHost : IWebviewHost
{
	public static readonly NullWebviewHost Instance = new();

	private NullWebviewHost() {
	}

	public bool IsAvailable() => false;

	public bool IsShowing(string id) => false;

	public void Show(string id, string html, string? rootDirectory, PixelRect bounds) {
	}

	public void SetBounds(string id, PixelRect bounds) {
	}

	public void Hide(string id) {
	}

	public void HideAll() {
	}

	public void ReleaseFocus() {
	}

	public void SetZoom(float factor) {
	}

	public void PostMessage(string id, string json) {
	}

	public void OnMessage(Action<string, string> handler) {
	}

	public void OnFailure(Action<string> handler) {
	}
}
