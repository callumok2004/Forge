using System.Runtime.InteropServices;

using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Core;

namespace Forge.UI;

public sealed class WindowEvents
{
	public Action<int, int>? Resized;

	public Action<PixelRect>? Render;
	public Action<int, int, int>? MouseWheel;

	public Action<int, int, int>? MouseHorizontalWheel;
	public Action<int, int>? MouseMove;

	public Action<int, int>? TitleBarMouseMove;

	public Action? TitleBarMouseLeave;

	public Action<int, int>? MouseDown;
	public Action<int, int>? MouseUp;
	public Action<uint>? KeyDown;
	public Action<char>? CharacterTyped;
	public Action<uint>? DpiChanged;
	public Action<nuint>? Timer;
	public Action? Closed;

	public Action? PlacementChanged;
}

public sealed unsafe class AppWindow : IAppWindow, IDisposable
{
	private const string ClassName = "ForgeWindow";

	private static AppWindow? _current;

	private bool _disposed;

	private readonly bool _startMaximized;

	private bool _inSizeMove;

	public AppWindow(string title, int width, int height, WindowState? state = null) {
		_current = this;
		Events = new WindowEvents();

		nint instance = Win32.GetModuleHandle(null);
		nint arrow = Win32.LoadCursor(0, IdcArrow());

		nint largeIcon = LoadApplicationIcon(instance, Win32.SmCxIcon, Win32.SmCyIcon);
		nint smallIcon = LoadApplicationIcon(instance, Win32.SmCxSmallIcon, Win32.SmCySmallIcon);

		fixed (char* className = ClassName) {
			Win32.WindowClassEx windowClass = new() {
				Size = (uint)sizeof(Win32.WindowClassEx),
				Style = Win32.CsHorizontalRedraw | Win32.CsVerticalRedraw | Win32.CsOwnDeviceContext,
				WindowProcedure = &WindowProcedure,
				Instance = instance,
				Cursor = arrow,
				Icon = largeIcon,
				SmallIcon = smallIcon,

				Background = 0,
				ClassName = className,
			};

			if (Win32.RegisterClassEx(in windowClass) == 0) {
				int error = Marshal.GetLastWin32Error();

				if (error != 1410) {
					throw new InvalidOperationException($"RegisterClassEx failed with Win32 error {error}.");
				}
			}
		}

		const int useDefault = unchecked((int)0x80000000);

		bool restore = state is { HasPosition: true } && IsOnScreen(state);

		Handle = Win32.CreateWindowEx(
			0,
			ClassName,
			title,
			Win32.WsOverlappedWindow | Win32.WsClipChildren | Win32.WsClipSiblings,
			restore ? state!.X : useDefault,
			restore ? state!.Y : useDefault,
			restore ? state!.Width : width,
			restore ? state!.Height : height,
			0,
			0,
			instance,
			0);

		_startMaximized = state?.IsMaximized ?? false;

		if (Handle == 0) {
			throw new InvalidOperationException(
				$"CreateWindowEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
		}

		Dpi = Win32.GetDpiForWindow(Handle);
	}

	private static nint LoadApplicationIcon(nint instance, int widthMetric, int heightMetric) =>
		Win32.LoadImage(instance, "#1", Win32.ImageIcon, Win32.GetSystemMetrics(widthMetric),
			Win32.GetSystemMetrics(heightMetric), Win32.LrShared);

	public nint Handle { get; private set; }

	public bool IsForeground() => Handle != 0 && Win32.GetForegroundWindow() == Handle;

	public nint GetHandle() => Handle;

	public uint GetDpi() => Dpi;

	public float GetDpiScale() => DpiScale;

	public int GetClientWidth() => ClientWidth;

	public int GetClientHeight() => ClientHeight;

	bool IAppWindow.IsMaximized() => IsMaximized;

	public WindowEvents Events { get; }

	public nint Cursor { get; set; }

	public static nint SystemCursor(int id) => Win32.LoadCursor(0, id);

	#region Custom frame

	public Func<int, int, nint>? HitTestTitleBar { get; set; }

	public bool IsMaximized {
		get {
			Win32.WindowPlacement placement = new() { Length = (uint)sizeof(Win32.WindowPlacement) };
			return Win32.GetWindowPlacement(Handle, ref placement) &&
				placement.ShowCommand == (uint)Win32.SwShowMaximized;
		}
	}

	public void Minimize() => Win32.PostMessage(Handle, Win32.WmSysCommand, (nint)Win32.ScMinimize, 0);

	public void ToggleMaximize() =>
		Win32.PostMessage(Handle, Win32.WmSysCommand,
			(nint)(IsMaximized ? Win32.ScRestore : Win32.ScMaximize), 0);

	public void CloseWindow() => Win32.PostMessage(Handle, Win32.WmSysCommand, (nint)Win32.ScClose, 0);

	private int ResizeBorderThickness =>
		Win32.GetSystemMetricsForDpi(Win32.SmCxFrame, Dpi) +
		Win32.GetSystemMetricsForDpi(Win32.SmCxPaddedBorder, Dpi);

	private void AdjustForCustomFrame(nint hwnd, nint lParam) {
		Win32.Rect* rects = (Win32.Rect*)lParam;
		int original = rects->Top;

		Win32.DefWindowProc(hwnd, Win32.WmNcCalcSize, 1, lParam);
		rects->Top = IsMaximized ? original + ResizeBorderThickness : original;
	}

	private nint HitTestFrame(nint hwnd, int screenX, int screenY) {
		Win32.Point point = new() { X = screenX, Y = screenY };
		Win32.ScreenToClient(hwnd, ref point);

		if (!IsMaximized && point.Y < ResizeBorderThickness) return Win32.HtTop;

		return HitTestTitleBar?.Invoke(point.X, point.Y) ?? Win32.HtClient;
	}

	#endregion

	public uint Dpi { get; private set; }

	public float DpiScale => Dpi / 96f;

	public int ClientWidth { get; private set; }

	public int ClientHeight { get; private set; }

	private static bool IsOnScreen(WindowState state) {
		int left = Win32.GetSystemMetrics(Win32.SmXVirtualScreen);
		int top = Win32.GetSystemMetrics(Win32.SmYVirtualScreen);
		int right = left + Win32.GetSystemMetrics(Win32.SmCxVirtualScreen);
		int bottom = top + Win32.GetSystemMetrics(Win32.SmCyVirtualScreen);

		return state.X < right && state.X + state.Width > left &&
			state.Y < bottom && state.Y + state.Height > top;
	}

	public WindowState CaptureState() {
		Win32.WindowPlacement placement = new() { Length = (uint)sizeof(Win32.WindowPlacement) };

		if (!Win32.GetWindowPlacement(Handle, ref placement)) return new WindowState();

		Win32.Rect bounds = placement.NormalPosition;

		return new WindowState {
			X = bounds.Left,
			Y = bounds.Top,
			Width = bounds.Right - bounds.Left,
			Height = bounds.Bottom - bounds.Top,
			IsMaximized = placement.ShowCommand == (uint)Win32.SwShowMaximized,
		};
	}

	public void Show() {
		Win32.SetWindowPos(Handle, 0, 0, 0, 0, 0,
			Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoZOrder | Win32.SwpNoOwnerZOrder |
			Win32.SwpFrameChanged);

		Win32.ShowWindow(Handle, _startMaximized ? Win32.SwShowMaximized : Win32.SwShow);
		Win32.UpdateWindow(Handle);
	}

	public void Invalidate() => Win32.InvalidateRect(Handle, 0, false);

	public void Invalidate(PixelRect area) {
		if (area.IsEmpty) return;

		Win32.Rect rect = new() {
			Left = area.Left,
			Top = area.Top,
			Right = area.Right,
			Bottom = area.Bottom,
		};

		Win32.InvalidateRect(Handle, in rect, false);
	}

	public void SetTitle(string title) => Win32.SetWindowText(Handle, title);

	public void StartTimer(nuint id, uint intervalMs) => Win32.SetTimer(Handle, id, intervalMs, 0);

	public void StopTimer(nuint id) => Win32.KillTimer(Handle, id);

	public static uint CaretBlinkMs => Win32.GetCaretBlinkTime();

	public static int RunMessageLoop() {
		while (true) {
			int result = Win32.GetMessage(out Win32.Message message, 0, 0, 0);
			if (result == 0) return (int)message.WParam;

			if (result == -1) {
				throw new InvalidOperationException(
					$"GetMessage failed with Win32 error {Marshal.GetLastWin32Error()}.");
			}

			Win32.TranslateMessage(in message);
			Win32.DispatchMessage(in message);
		}
	}

	private static nint IdcArrow() => Win32.IdcArrow;

	public static Action<Exception>? FatalHandler { get; set; }

	[UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
	private static nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam) {
		try {
			return Dispatch(hwnd, message, wParam, lParam);
		}
		catch (Exception exception) {
			FatalHandler?.Invoke(exception);
			Win32.PostQuitMessage(1);
			return 0;
		}
	}

	private static nint Dispatch(nint hwnd, uint message, nint wParam, nint lParam) {
		AppWindow? window = _current;
		if (window is null) return Win32.DefWindowProc(hwnd, message, wParam, lParam);

		switch (message) {
			case Win32.WmNcCalcSize when wParam != 0:
				window.AdjustForCustomFrame(hwnd, lParam);
				return 0;

			case Win32.WmNcHitTest: {
					nint standard = Win32.DefWindowProc(hwnd, message, wParam, lParam);
					if (standard != Win32.HtClient) return standard;

					return window.HitTestFrame(hwnd, Win32.LowWord(lParam), Win32.HighWord(lParam));
				}

			case Win32.WmEnterSizeMove:
				window._inSizeMove = true;
				return 0;

			case Win32.WmExitSizeMove:
				window._inSizeMove = false;
				window.Events.PlacementChanged?.Invoke();
				return 0;

			case Win32.WmSize:
				if (!window._inSizeMove && (uint)wParam is 0 or 2) window.Events.PlacementChanged?.Invoke();

				if ((uint)wParam != Win32.SizeMinimized) {
					window.ClientWidth = Win32.LowWord(lParam);
					window.ClientHeight = Win32.HighWord(lParam);
					window.Events.Resized?.Invoke(window.ClientWidth, window.ClientHeight);
				}

				return 0;

			case Win32.WmPaint: {
					PixelRect damage = Win32.GetUpdateRect(hwnd, out Win32.Rect update, false)
						? new PixelRect(update.Left, update.Top, update.Right, update.Bottom)
						: PixelRect.Empty;

					if (!damage.IsEmpty) window.Events.Render?.Invoke(damage);

					Win32.ValidateRect(hwnd, 0);
					return 0;
				}

			case Win32.WmEraseBackground:
				return 1;

			case Win32.WmMouseWheel: {
					Win32.Point point = new() { X = Win32.LowWord(lParam), Y = Win32.HighWord(lParam) };
					Win32.ScreenToClient(hwnd, ref point);

					window.Events.MouseWheel?.Invoke(Win32.WheelDelta(wParam), point.X, point.Y);
					return 0;
				}

			case Win32.WmMouseHWheel: {
					Win32.Point point = new() { X = Win32.LowWord(lParam), Y = Win32.HighWord(lParam) };
					Win32.ScreenToClient(hwnd, ref point);

					window.Events.MouseHorizontalWheel?.Invoke(Win32.WheelDelta(wParam), point.X, point.Y);
					return 0;
				}

			case Win32.WmMouseMove:
				window.Events.TitleBarMouseLeave?.Invoke();
				window.Events.MouseMove?.Invoke(Win32.LowWord(lParam), Win32.HighWord(lParam));
				return 0;

			case Win32.WmNcMouseMove: {
					Win32.Point point = new() { X = Win32.LowWord(lParam), Y = Win32.HighWord(lParam) };
					Win32.ScreenToClient(hwnd, ref point);

					window.Events.TitleBarMouseMove?.Invoke(point.X, point.Y);
					break;
				}

			case Win32.WmNcMouseLeave:
				window.Events.TitleBarMouseLeave?.Invoke();
				break;

			case Win32.WmNcLeftButtonDown
				when wParam == Win32.HtMinButton || wParam == Win32.HtMaxButton || wParam == Win32.HtClose:
				return 0;

			case Win32.WmNcLeftButtonUp when wParam == Win32.HtMinButton:
				window.Minimize();
				return 0;

			case Win32.WmNcLeftButtonUp when wParam == Win32.HtMaxButton:
				window.ToggleMaximize();
				return 0;

			case Win32.WmNcLeftButtonUp when wParam == Win32.HtClose:
				window.CloseWindow();
				return 0;

			case Win32.WmSetCursor when (Win32.LowWord(lParam) & 0xFFFF) == Win32.HtClient:
				Win32.SetCursor(window.Cursor);
				return 1;

			case Win32.WmLeftButtonDown:
				Win32.SetCapture(hwnd);
				window.Events.MouseDown?.Invoke(Win32.LowWord(lParam), Win32.HighWord(lParam));
				return 0;

			case Win32.WmLeftButtonUp:
				Win32.ReleaseCapture();
				window.Events.MouseUp?.Invoke(Win32.LowWord(lParam), Win32.HighWord(lParam));
				return 0;

			case Win32.WmKeyDown:
				window.Events.KeyDown?.Invoke((uint)wParam);
				return 0;

			case Win32.WmSysKeyDown:
				window.Events.KeyDown?.Invoke((uint)wParam);
				return 0;

			case Win32.WmChar:
				window.Events.CharacterTyped?.Invoke((char)wParam);
				return 0;

			case Win32.WmTimer:
				window.Events.Timer?.Invoke((nuint)wParam);
				return 0;

			case Win32.WmDpiChanged:
				window.Dpi = (uint)Win32.LowWord(wParam);
				window.Events.DpiChanged?.Invoke(window.Dpi);
				return 0;

			case Win32.WmClose:
				window.Events.PlacementChanged?.Invoke();
				break;

			case Win32.WmDestroy:
				window.Events.Closed?.Invoke();
				Win32.PostQuitMessage(0);
				return 0;
		}

		return Win32.DefWindowProc(hwnd, message, wParam, lParam);
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		if (Handle != 0) {
			Win32.DestroyWindow(Handle);
			Handle = 0;
		}

		if (ReferenceEquals(_current, this)) _current = null;
	}
}
