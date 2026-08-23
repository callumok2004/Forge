using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public static unsafe partial class Win32
{
	public const string User32 = "user32.dll";
	public const string Kernel32 = "kernel32.dll";
	public const string Shcore = "shcore.dll";

	#region Window styles and messages

	public const uint WsOverlapped = 0x00000000;
	public const uint WsCaption = 0x00C00000;
	public const uint WsSysMenu = 0x00080000;
	public const uint WsThickFrame = 0x00040000;
	public const uint WsMinimizeBox = 0x00020000;
	public const uint WsMaximizeBox = 0x00010000;
	public const uint WsVisible = 0x10000000;
	public const uint WsClipChildren = 0x02000000;
	public const uint WsClipSiblings = 0x04000000;

	public const uint WsOverlappedWindow =
		WsOverlapped | WsCaption | WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;

	public const uint WmDestroy = 0x0002;
	public const uint WmSize = 0x0005;
	public const uint WmSetFocus = 0x0007;

	[LibraryImport("user32.dll")]
	public static partial nint SetFocus(nint window);
	public const uint WmKillFocus = 0x0008;
	public const uint WmPaint = 0x000F;
	public const uint WmClose = 0x0010;
	public const uint WmEraseBackground = 0x0014;
	public const uint WmKeyDown = 0x0100;
	public const uint WmKeyUp = 0x0101;
	public const uint WmChar = 0x0102;
	public const uint WmSysKeyDown = 0x0104;
	public const uint WmSetCursor = 0x0020;

	public const uint WmMouseMove = 0x0200;
	public const uint WmLeftButtonDown = 0x0201;
	public const uint WmLeftButtonUp = 0x0202;
	public const uint WmMouseWheel = 0x020A;
	public const uint WmMouseHWheel = 0x020E;
	public const uint WmTimer = 0x0113;
	public const uint WmDpiChanged = 0x02E0;
	public const uint WmGetMinMaxInfo = 0x0024;
	public const uint WmActivate = 0x0006;
	public const uint WmNcCalcSize = 0x0083;
	public const uint WmNcHitTest = 0x0084;
	public const uint WmNcLeftButtonDown = 0x00A1;
	public const uint WmNcLeftButtonUp = 0x00A2;
	public const uint WmNcMouseMove = 0x00A0;
	public const uint WmNcMouseLeave = 0x02A2;
	public const uint WmSysCommand = 0x0112;

	public const uint WmExitSizeMove = 0x0232;

	public const uint WmEnterSizeMove = 0x0231;

	#region Hit-test results

	public const nint HtClient = 1;
	public const nint HtCaption = 2;
	public const nint HtLeft = 10;
	public const nint HtRight = 11;
	public const nint HtTop = 12;
	public const nint HtTopLeft = 13;
	public const nint HtTopRight = 14;
	public const nint HtBottom = 15;
	public const nint HtBottomLeft = 16;
	public const nint HtBottomRight = 17;

	public const nint HtMinButton = 8;
	public const nint HtMaxButton = 9;
	public const nint HtClose = 20;

	#endregion

	public const uint ScMinimize = 0xF020;
	public const uint ScMaximize = 0xF030;
	public const uint ScRestore = 0xF120;
	public const uint ScClose = 0xF060;

	public const int SwShow = 5;
	public const int SwShowNormal = 1;

	public const uint SizeMinimized = 1;

	public const uint CsHorizontalRedraw = 0x0002;
	public const uint CsVerticalRedraw = 0x0001;
	public const uint CsOwnDeviceContext = 0x0020;

	public const int IdcArrow = 32512;
	public const int IdcIBeam = 32513;

	public const int IdcSizeWestEast = 32644;
	public const int IdcSizeNorthSouth = 32645;

	#endregion

	#region Structures

	[StructLayout(LayoutKind.Sequential)]
	public struct Point
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct Rect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;

		public readonly int Width => Right - Left;

		public readonly int Height => Bottom - Top;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct Message
	{
		public nint Hwnd;
		public uint Value;
		public nint WParam;
		public nint LParam;
		public uint Time;
		public Point Point;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct WindowClassEx
	{
		public uint Size;
		public uint Style;
		public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> WindowProcedure;
		public int ClassExtra;
		public int WindowExtra;
		public nint Instance;
		public nint Icon;
		public nint Cursor;
		public nint Background;
		public char* MenuName;
		public char* ClassName;
		public nint SmallIcon;
	}

	#endregion

	#region Imports

	[LibraryImport(User32, EntryPoint = "RegisterClassExW", SetLastError = true)]
	public static partial ushort RegisterClassEx(in WindowClassEx windowClass);

	[LibraryImport(User32, EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	public static partial nint CreateWindowEx(
		uint exStyle,
		string className,
		string windowName,
		uint style,
		int x,
		int y,
		int width,
		int height,
		nint parent,
		nint menu,
		nint instance,
		nint param);

	[LibraryImport(User32, EntryPoint = "DefWindowProcW")]
	public static partial nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

	[LibraryImport(User32, EntryPoint = "ShowWindow")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool ShowWindow(nint hwnd, int command);

	[LibraryImport(User32, EntryPoint = "UpdateWindow")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool UpdateWindow(nint hwnd);

	[LibraryImport(User32, EntryPoint = "DestroyWindow")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool DestroyWindow(nint hwnd);

	[LibraryImport(User32, EntryPoint = "GetMessageW")]
	public static partial int GetMessage(out Message message, nint hwnd, uint filterMin, uint filterMax);

	[LibraryImport(User32, EntryPoint = "PeekMessageW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool PeekMessage(out Message message, nint hwnd, uint filterMin, uint filterMax, uint remove);

	[LibraryImport(User32, EntryPoint = "TranslateMessage")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool TranslateMessage(in Message message);

	[LibraryImport(User32, EntryPoint = "DispatchMessageW")]
	public static partial nint DispatchMessage(in Message message);

	[LibraryImport(User32, EntryPoint = "PostQuitMessage")]
	public static partial void PostQuitMessage(int exitCode);

	[LibraryImport(User32, EntryPoint = "ScreenToClient")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool ScreenToClient(nint hwnd, ref Point point);

	[LibraryImport(User32, EntryPoint = "GetClientRect")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetClientRect(nint hwnd, out Rect rect);

	[LibraryImport(User32, EntryPoint = "InvalidateRect")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

	[LibraryImport(User32, EntryPoint = "InvalidateRect")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool InvalidateRect(nint hwnd, in Rect rect,
		[MarshalAs(UnmanagedType.Bool)] bool erase);

	[LibraryImport(User32, EntryPoint = "GetUpdateRect")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetUpdateRect(nint hwnd, out Rect rect,
		[MarshalAs(UnmanagedType.Bool)] bool erase);

	[LibraryImport(User32, EntryPoint = "ValidateRect")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool ValidateRect(nint hwnd, nint rect);

	[LibraryImport(User32, EntryPoint = "LoadCursorW", SetLastError = true)]
	public static partial nint LoadCursor(nint instance, nint cursorName);

	[LibraryImport(User32, EntryPoint = "SetCursor")]
	public static partial nint SetCursor(nint cursor);

	[LibraryImport(User32, EntryPoint = "LoadImageW", SetLastError = true,
		StringMarshalling = StringMarshalling.Utf16)]
	public static partial nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint load);

	public const uint ImageIcon = 1;
	public const uint LrDefaultSize = 0x00000040;
	public const uint LrShared = 0x00008000;

	[LibraryImport(User32, EntryPoint = "GetSystemMetrics")]
	public static partial int GetSystemMetrics(int index);

	[LibraryImport(User32, EntryPoint = "GetSystemMetricsForDpi")]
	public static partial int GetSystemMetricsForDpi(int index, uint dpi);

	[LibraryImport(User32, EntryPoint = "SetWindowPos", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy,
		uint flags);

	public const uint SwpNoSize = 0x0001;
	public const uint SwpNoMove = 0x0002;
	public const uint SwpNoZOrder = 0x0004;
	public const uint SwpFrameChanged = 0x0020;
	public const uint SwpNoOwnerZOrder = 0x0200;

	[LibraryImport(User32, EntryPoint = "PostMessageW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

	[LibraryImport(User32, EntryPoint = "GetWindowPlacement")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

	public struct WindowPlacement
	{
		public uint Length;
		public uint Flags;
		public uint ShowCommand;
		public Point MinPosition;
		public Point MaxPosition;
		public Rect NormalPosition;
	}

	public const int SwShowMaximized = 3;

	public const int SmXVirtualScreen = 76;
	public const int SmYVirtualScreen = 77;
	public const int SmCxVirtualScreen = 78;
	public const int SmCyVirtualScreen = 79;

	public const int SmCxFrame = 32;
	public const int SmCyFrame = 33;
	public const int SmCxPaddedBorder = 92;

	public const int SmCxIcon = 11;
	public const int SmCyIcon = 12;
	public const int SmCxSmallIcon = 49;
	public const int SmCySmallIcon = 50;

	[LibraryImport(User32, EntryPoint = "GetDpiForWindow")]
	public static partial uint GetDpiForWindow(nint hwnd);

	[LibraryImport(User32, EntryPoint = "SetTimer")]
	public static partial nuint SetTimer(nint hwnd, nuint id, uint intervalMs, nint callback);

	[LibraryImport(User32, EntryPoint = "KillTimer")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool KillTimer(nint hwnd, nuint id);

	[LibraryImport(User32, EntryPoint = "GetCaretBlinkTime")]
	public static partial uint GetCaretBlinkTime();

	[LibraryImport(User32, EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool SetWindowText(nint hwnd, string text);

	[LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
	public static partial nint GetModuleHandle(string? moduleName);

	[LibraryImport(User32)]
	public static partial nint GetForegroundWindow();

	public const int VkShift = 0x10;
	public const int VkControl = 0x11;

	public const int VkMenu = 0x12;

	[LibraryImport(User32, EntryPoint = "GetKeyState")]
	public static partial short GetKeyState(int virtualKey);

	[LibraryImport(User32, EntryPoint = "GetDoubleClickTime")]
	public static partial uint GetDoubleClickTime();

	public static long DoubleClickTime => GetDoubleClickTime();

	public static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

	[LibraryImport(User32, EntryPoint = "SetCapture")]
	public static partial nint SetCapture(nint hwnd);

	[LibraryImport(User32, EntryPoint = "ReleaseCapture")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool ReleaseCapture();

	public const uint MbIconError = 0x00000010;

	[LibraryImport(User32, EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
	public static partial int MessageBox(nint hwnd, string text, string caption, uint type);

	private const uint MbConfirmDestructive = 0x00000004 | 0x00000030 | 0x00000100;

	private const int IdYes = 6;

	public static bool Confirm(string caption, string text) =>
		MessageBox(0, text, caption, MbConfirmDestructive) == IdYes;

	#endregion

	#region Message parameter helpers

	public static int LowWord(nint value) => unchecked((short)(long)value);

	public static int HighWord(nint value) => unchecked((short)((long)value >> 16));

	public static int WheelDelta(nint wParam) => unchecked((short)((long)wParam >> 16));

	#endregion
}
