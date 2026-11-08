using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public static unsafe partial class RecycleBin
{
	private const uint Delete = 3;
	private const ushort Silent = 0x0004;
	private const ushort NoConfirmation = 0x0010;
	private const ushort AllowUndo = 0x0040;
	private const ushort NoErrorUi = 0x0400;

	[StructLayout(LayoutKind.Sequential)]
	private struct FileOperation
	{
		public nint Window;
		public uint Function;
		public char* From;
		public char* To;
		public ushort Flags;
		public int Aborted;
		public nint NameMappings;
		public char* ProgressTitle;
	}

	[LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
	private static partial int SHFileOperation(FileOperation* operation);

	public static bool Send(string path) {
		string from = path + "\0\0";

		fixed (char* pointer = from) {
			FileOperation operation = new() { Function = Delete, From = pointer, Flags = (ushort)(AllowUndo | NoConfirmation | Silent | NoErrorUi) };
			return SHFileOperation(&operation) == 0 && operation.Aborted == 0;
		}
	}
}
