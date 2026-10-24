using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public static unsafe partial class Win32
{
	public const string Gdi32 = "gdi32.dll";

	public const int RegionDifference = 4;

	public const uint WsChild = 0x40000000;

	public const uint SwpNoActivate = 0x0010;

	[LibraryImport(Gdi32, EntryPoint = "CreateRectRgn")]
	public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

	[LibraryImport(Gdi32, EntryPoint = "CombineRgn")]
	public static partial int CombineRgn(nint destination, nint first, nint second, int mode);

	[LibraryImport(Gdi32, EntryPoint = "DeleteObject")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool DeleteObject(nint handle);

	[LibraryImport(User32, EntryPoint = "SetWindowRgn")]
	public static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
