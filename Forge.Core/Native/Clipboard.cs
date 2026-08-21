using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public static unsafe partial class Clipboard
{
	private const uint UnicodeText = 13;
	private const uint GlobalMovable = 0x0002;

	[LibraryImport(Win32.User32, EntryPoint = "OpenClipboard")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool OpenClipboard(nint owner);

	[LibraryImport(Win32.User32, EntryPoint = "CloseClipboard")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseClipboard();

	[LibraryImport(Win32.User32, EntryPoint = "EmptyClipboard")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EmptyClipboard();

	[LibraryImport(Win32.User32, EntryPoint = "GetClipboardData")]
	private static partial nint GetClipboardData(uint format);

	[LibraryImport(Win32.User32, EntryPoint = "SetClipboardData")]
	private static partial nint SetClipboardData(uint format, nint data);

	[LibraryImport(Win32.User32, EntryPoint = "IsClipboardFormatAvailable")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsClipboardFormatAvailable(uint format);

	[LibraryImport(Win32.Kernel32, EntryPoint = "GlobalAlloc")]
	private static partial nint GlobalAlloc(uint flags, nuint bytes);

	[LibraryImport(Win32.Kernel32, EntryPoint = "GlobalFree")]
	private static partial nint GlobalFree(nint handle);

	[LibraryImport(Win32.Kernel32, EntryPoint = "GlobalLock")]
	private static partial nint GlobalLock(nint handle);

	[LibraryImport(Win32.Kernel32, EntryPoint = "GlobalUnlock")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GlobalUnlock(nint handle);

	public static string GetText() {
		if (!IsClipboardFormatAvailable(UnicodeText) || !OpenClipboard(0)) return string.Empty;

		try {
			nint handle = GetClipboardData(UnicodeText);
			if (handle == 0) return string.Empty;

			nint pointer = GlobalLock(handle);
			if (pointer == 0) return string.Empty;

			try {
				return Marshal.PtrToStringUni(pointer) ?? string.Empty;
			}
			finally {
				GlobalUnlock(handle);
			}
		}
		finally {
			CloseClipboard();
		}
	}

	public static bool SetText(ReadOnlySpan<char> text) {
		if (!OpenClipboard(0)) return false;

		try {
			EmptyClipboard();

			nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
			nint handle = GlobalAlloc(GlobalMovable, bytes);
			if (handle == 0) return false;

			nint pointer = GlobalLock(handle);
			if (pointer == 0) {
				GlobalFree(handle);
				return false;
			}

			try {
				Span<char> destination = new((void*)pointer, text.Length + 1);
				text.CopyTo(destination);
				destination[text.Length] = '\0';
			}
			finally {
				GlobalUnlock(handle);
			}

			if (SetClipboardData(UnicodeText, handle) == 0) {
				GlobalFree(handle);
				return false;
			}

			return true;
		}
		finally {
			CloseClipboard();
		}
	}
}
