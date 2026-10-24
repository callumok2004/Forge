using System.Drawing;
using System.Runtime.InteropServices;

using Forge.Core;
using Forge.Core.Native;
using Forge.Common.Webview;

using Microsoft.Web.WebView2.Core;

namespace Forge.Webview;

public sealed class WebviewHost(nint window, string userDataFolder, Action<Action> post)
	: IWebviewHost, IDisposable
{
	private const string BrowserArguments =
		"--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection,Translate " +
		"--renderer-process-limit=1 --disable-background-timer-throttling " +
		"--js-flags=--max-old-space-size=192";

	private const string VirtualHost = "forge-webview";

	private const string PageHost = "forge-view";

	private const string IndexUrl = "https://forge-view/index.html";

	private const string ApiScript = """
		(function () {
			if (window.acquireVsCodeApi) return;

			let acquired = false;
			let state = undefined;

			try {
				const stored = window.sessionStorage.getItem("forge.webview.state");
				if (stored) state = JSON.parse(stored);
			}
			catch { }

			window.acquireVsCodeApi = function () {
				if (acquired) throw new Error("An instance of the VS Code API has already been acquired");

				acquired = true;

				return {
					postMessage: function (message, transfer) {
						window.chrome.webview.postMessage(message);
					},
					getState: function () {
						return state;
					},
					setState: function (value) {
						state = value;

						try {
							window.sessionStorage.setItem("forge.webview.state", JSON.stringify(value));
						}
						catch { }

						return value;
					},
				};
			};

			window.chrome.webview.addEventListener("message", function (event) {
				window.postMessage(event.data, "*");
			});

			const applyKind = function () {
				if (!document.body) return;

				document.body.classList.add("vscode-dark");
				document.body.dataset.vscodeThemeKind = "vscode-dark";
				document.body.dataset.vscodeThemeName = "Forge Dark";
			};

			if (document.readyState === "loading") {
				document.addEventListener("DOMContentLoaded", applyKind);
			}
			else applyKind();
		})();
		""";

	private sealed class View
	{
		public string Html = string.Empty;
		public string? Overrides;
		public string? Root;
		public PixelRect Bounds;
		public bool Wanted;
		public bool Loaded;
		public CoreWebView2Controller? Controller;
		public nint Container;
		public List<string>? Queued;
	}

	private PixelRect[] _occlusions = [];

	public void SetOcclusions(ReadOnlySpan<PixelRect> areas) {
		_occlusions = areas.ToArray();

		foreach (View view in _views.Values)
			if (view.Container != 0) ApplyRegion(view);
	}

	private void Place(View view) {
		if (view.Container == 0) return;

		PixelRect bounds = view.Bounds;
		_ = Win32.SetWindowPos(view.Container, 0, bounds.Left, bounds.Top, Math.Max(bounds.Width, 0), Math.Max(bounds.Height, 0), Win32.SwpNoZOrder | Win32.SwpNoActivate);

		if (view.Controller is { } controller) controller.Bounds = new Rectangle(0, 0, Math.Max(bounds.Width, 0), Math.Max(bounds.Height, 0));

		ApplyRegion(view);
	}

	private void ApplyRegion(View view) {
		PixelRect bounds = view.Bounds;
		nint region = 0;

		foreach (PixelRect occluder in _occlusions) {
			PixelRect overlap = occluder.Intersect(bounds);

			if (overlap.IsEmpty) continue;

			if (region == 0) region = Win32.CreateRectRgn(0, 0, bounds.Width, bounds.Height);

			nint hole = Win32.CreateRectRgn(overlap.Left - bounds.Left, overlap.Top - bounds.Top, overlap.Right - bounds.Left, overlap.Bottom - bounds.Top);
			_ = Win32.CombineRgn(region, region, hole, Win32.RegionDifference);
			_ = Win32.DeleteObject(hole);
		}

		_ = Win32.SetWindowRgn(view.Container, region, true);
	}

	private nint CreateContainer(PixelRect bounds) => Win32.CreateWindowEx(0, "STATIC", string.Empty, Win32.WsChild | Win32.WsVisible | Win32.WsClipChildren | Win32.WsClipSiblings, bounds.Left, bounds.Top, Math.Max(bounds.Width, 1), Math.Max(bounds.Height, 1), window, 0, Win32.GetModuleHandle(null), 0);

	private readonly Dictionary<string, View> _views = new(StringComparer.Ordinal);

	private CoreWebView2Environment? _environment;
	private bool _creating;
	private bool _unavailable;
	private bool _disposed;

	public string ThemeCss { get; set; } = string.Empty;

	private Action<string, string>? _message;
	private Action<string>? _failure;

	public void OnMessage(Action<string, string> handler) => _message += handler;

	public void OnFailure(Action<string> handler) => _failure += handler;

	public bool Trace {
		get;
		set {
			field = value;

			if (value) Directory.CreateDirectory(userDataFolder);
		}
	}

	public bool IsAvailable() {
		if (_unavailable) return false;

		try {
			return CoreWebView2Environment.GetAvailableBrowserVersionString() is { Length: > 0 };
		}
		catch (Exception exception) when (exception is WebView2RuntimeNotFoundException or DllNotFoundException
			or EntryPointNotFoundException) {
			_unavailable = true;
			return false;
		}
	}

	public bool IsShowing(string id) => _views.TryGetValue(id, out View? view) && view is { Wanted: true, Loaded: true };

	public void Show(string id, string html, string? rootDirectory, PixelRect bounds, string? extensionId = null) {
		if (_disposed || _unavailable) return;

		View view = Track(id);
		view.Overrides = WebviewTheme.OverridesFor(extensionId);

		bool changed = !string.Equals(view.Html, html, StringComparison.Ordinal);

		view.Html = html;
		view.Root = rootDirectory;
		view.Bounds = bounds;
		view.Wanted = true;

		if (view.Controller is { } controller) {
			Place(view);
			controller.IsVisible = true;

			if (changed) Navigate(id, view);

			return;
		}

		EnsureEnvironment();
		CreateController(id, view);
	}

	private float _zoom = 1f;

	public void SetZoom(float factor) {
		_zoom = Math.Clamp(factor, 0.25f, 5f);

		foreach (View view in _views.Values) {
			if (view.Controller is { } controller) controller.ZoomFactor = _zoom;
		}
	}

	public void ReleaseFocus() => Win32.SetFocus(window);

	public void SetBounds(string id, PixelRect bounds) {
		if (!_views.TryGetValue(id, out View? view)) return;

		view.Bounds = bounds;

		if (view.Controller is not null && view.Wanted) Place(view);
	}

	public void Hide(string id) {
		if (!_views.TryGetValue(id, out View? view)) return;

		view.Wanted = false;
		view.Loaded = false;

		Release(view);
	}

	public void HideAll() {
		foreach (View view in _views.Values) {
			view.Wanted = false;
			view.Loaded = false;

			Release(view);
		}
	}

	public void PostMessage(string id, string json) {
		if (!_views.TryGetValue(id, out View? view)) {
			if (Trace) Fail($"dropped [{id}] {json.Length} bytes, no such view");

			return;
		}

		if (view is { Loaded: true, Controller.CoreWebView2: { } core }) {
			if (Trace) Fail($"deliver [{id}] {json.Length} bytes");

			try {
				string literal = System.Text.Json.JsonSerializer.Serialize(json);

				_ = core.ExecuteScriptAsync(
					$"window.postMessage(JSON.parse({literal}), \"*\");");
			}
			catch (Exception exception) when (exception is ArgumentException or COMException
				or InvalidOperationException) {
				Fail($"Could not deliver a {json.Length} byte message to {id}: {exception.Message}");
			}

			return;
		}

		if (Trace) Fail($"queue [{id}] {json.Length} bytes (loaded: {view.Loaded}, controller: {view.Controller is not null})");

		(view.Queued ??= []).Add(json);
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		foreach (View view in _views.Values) Release(view);

		_views.Clear();
		_environment = null;
	}

	private View Track(string id) {
		if (_views.TryGetValue(id, out View? existing)) return existing;

		View created = new();
		_views[id] = created;

		return created;
	}

	private static Rectangle ToRectangle(in PixelRect bounds) =>
		new(bounds.Left, bounds.Top, Math.Max(bounds.Right - bounds.Left, 0),
			Math.Max(bounds.Bottom - bounds.Top, 0));

	private void EnsureEnvironment() {
		if (_environment is not null || _creating || _unavailable) return;

		if (!IsAvailable()) {
			Fail("The WebView2 runtime is not installed.");
			return;
		}

		_creating = true;

		CoreWebView2EnvironmentOptions options = new() {
			AdditionalBrowserArguments = BrowserArguments,
			AreBrowserExtensionsEnabled = false,
		};

		Complete(CoreWebView2Environment.CreateAsync(null, userDataFolder, options), environment => {
			_creating = false;
			_environment = environment;

			foreach (KeyValuePair<string, View> pair in _views) {
				if (pair.Value is { Wanted: true, Controller: null }) CreateController(pair.Key, pair.Value);
			}
		});
	}

	private void CreateController(string id, View view) {
		if (_environment is not { } environment || view.Controller is not null) return;

		if (view.Container == 0) view.Container = CreateContainer(view.Bounds);

		Complete(environment.CreateCoreWebView2ControllerAsync(view.Container), controller => {
			if (_disposed || !view.Wanted) {
				controller.Close();
				return;
			}

			view.Controller = controller;

			Configure(id, view, controller);

			Place(view);
			controller.ZoomFactor = _zoom;
			controller.IsVisible = true;

			ReleaseFocus();

			Complete(controller.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ApiScript),
				_ => Navigate(id, view));
		});
	}

	private void Configure(string id, View view, CoreWebView2Controller controller) {
		CoreWebView2 core = controller.CoreWebView2;
		CoreWebView2Settings settings = core.Settings;

		settings.AreDefaultContextMenusEnabled = false;
		settings.AreBrowserAcceleratorKeysEnabled = false;
		settings.IsStatusBarEnabled = false;
		settings.IsSwipeNavigationEnabled = false;
		settings.IsZoomControlEnabled = false;
		settings.AreDevToolsEnabled = false;
		settings.IsGeneralAutofillEnabled = false;
		settings.IsPasswordAutosaveEnabled = false;

		controller.DefaultBackgroundColor = Color.Transparent;

		core.SetVirtualHostNameToFolderMapping(PageHost, PageDirectory(id),
			CoreWebView2HostResourceAccessKind.Allow);

		if (view.Root is { Length: > 0 } root && Directory.Exists(root)) {
			core.SetVirtualHostNameToFolderMapping(VirtualHost, root,
				CoreWebView2HostResourceAccessKind.Allow);
		}

		core.WebMessageReceived += (_, args) => {
			string payload;

			try {
				payload = args.WebMessageAsJson;
			}
			catch (ArgumentException) {
				return;
			}

			_message?.Invoke(id, payload);
		};

		core.NavigationCompleted += (_, _) => {
			view.Loaded = true;

			if (Trace) Fail($"loaded [{id}], flushing {view.Queued?.Count ?? 0} queued");

			if (view.Queued is not { Count: > 0 } queued) return;

			foreach (string json in queued) core.PostWebMessageAsJson(json);

			queued.Clear();
		};

		core.NewWindowRequested += (_, args) => {
			args.Handled = true;

			if (args.Uri is { Length: > 0 } uri) OpenExternally(uri);
		};

		_ = core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");

		core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived +=
			(_, args) => Fail($"[{id}] page error: {Shorten(args.ParameterObjectAsJson)}");

		if (Trace) {
			_ = core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");

			core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived +=
				(_, args) => Fail($"console {Shorten(args.ParameterObjectAsJson)}");

			core.GetDevToolsProtocolEventReceiver("Log.entryAdded").DevToolsProtocolEventReceived +=
				(_, args) => Fail($"log {Shorten(args.ParameterObjectAsJson)}");
		}

		core.ProcessFailed += (_, args) => {
			view.Loaded = false;
			Fail($"Webview process failed: {args.ProcessFailedKind}.");
		};
	}

	private void Navigate(string id, View view) {
		if (view.Controller?.CoreWebView2 is not { } core) return;

		if (Trace) Fail($"navigate {view.Html.Length} bytes");

		view.Loaded = false;

		try {
			File.WriteAllText(Path.Combine(PageDirectory(id), "index.html"), WithTheme(view.Html, view.Overrides));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			Fail($"Could not stage the page for {id}: {exception.Message}");
			return;
		}

		if (string.Equals(core.Source, IndexUrl, StringComparison.Ordinal)) core.Reload();
		else core.Navigate(IndexUrl);
	}

	private string PageDirectory(string id) {
		string directory = Path.Combine(userDataFolder, "pages", Sanitise(id));

		Directory.CreateDirectory(directory);

		return directory;
	}

	private static string Sanitise(string id) {
		Span<char> buffer = stackalloc char[id.Length];

		for (int i = 0; i < id.Length; i++) {
			buffer[i] = char.IsLetterOrDigit(id[i]) || id[i] is '-' or '_' ? id[i] : '_';
		}

		return new string(buffer);
	}

	private string WithTheme(string html, string? overrides) {
		string nonce = NonceOf(html);
		string nonceAttribute = nonce.Length > 0 ? $" nonce=\"{nonce}\"" : string.Empty;

		if (overrides is not null) {
			string overrideStyle = $"<style id=\"_forgeOverrides\"{nonceAttribute}>{overrides}</style>";
			int headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);

			html = headEnd < 0 ? overrideStyle + html : html[..headEnd] + overrideStyle + html[headEnd..];
		}

		string prologue = $"<base href=\"https://{VirtualHost}/\"><style id=\"forge-theme\"{nonceAttribute}>{ThemeCss}</style>";

		int head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);

		if (head < 0) return prologue + html;

		int close = html.IndexOf('>', head);

		return close < 0 ? prologue + html : html[..(close + 1)] + prologue + html[(close + 1)..];
	}

	private static string NonceOf(string html) {
		const string marker = "nonce-";

		int start = html.IndexOf(marker, StringComparison.Ordinal);

		if (start < 0) return string.Empty;

		start += marker.Length;
		int end = start;

		while (end < html.Length && (char.IsLetterOrDigit(html[end]) || html[end] is '+' or '/' or '=' or '-' or '_')) {
			end++;
		}

		return html[start..end];
	}

	private static string Shorten(string value) => value.Length > 600 ? value[..600] + "..." : value;

	private static void Release(View view) {
		if (view.Controller is { } controller) {
			view.Controller = null;

			try {
				controller.IsVisible = false;
				controller.Close();
			}
			catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) {
			}
		}

		if (view.Container == 0) return;

		_ = Win32.DestroyWindow(view.Container);
		view.Container = 0;
	}

	private void Complete<T>(Task<T> task, Action<T> continuation) =>
		task.ContinueWith(finished => post(() => {
			if (finished.IsFaulted) {
				Fail(finished.Exception?.GetBaseException().Message ?? "Webview creation failed.");
				_creating = false;
				return;
			}

			try {
				continuation(finished.Result);
			}
			catch (Exception exception) when (exception is COMException or InvalidOperationException
				or ObjectDisposedException) {
				Fail(exception.Message);
			}
		}), TaskScheduler.Default);

	private void Fail(string reason) {
		if (Trace) {
			try {
				File.AppendAllText(Path.Combine(userDataFolder, "webview.log"),
					$"{DateTime.Now:HH:mm:ss.fff} {reason}{Environment.NewLine}");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
				or DirectoryNotFoundException) {
			}
		}

		_failure?.Invoke(reason);
	}

	private static void OpenExternally(string uri) {
		try {
			using System.Diagnostics.Process? _ = System.Diagnostics.Process.Start(
				new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
			or InvalidOperationException) {
		}
	}
}
