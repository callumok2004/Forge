using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public static unsafe partial class FileDialog
{
	private const string Ole32 = "ole32.dll";

	private static readonly Guid FileOpenDialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
	private static readonly Guid FileSaveDialogClass = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
	private static readonly Guid FileDialogInterface = new("42F85136-DB7E-439C-85F1-E4075D135FC8");

	private const uint InProcServer = 1;

	#region FILEOPENDIALOGOPTIONS

	private const uint OverwritePrompt = 0x00000002;
	private const uint ForceFileSystem = 0x00000040;
	private const uint PathMustExist = 0x00000800;
	private const uint FileMustExist = 0x00001000;
	private const uint PickFolders = 0x00000020;

	#endregion

	#region Vtable slots

	private const int Release = 2;
	private const int Show = 3;

	private const int SetOptions = 9;
	private const int GetOptions = 10;
	private const int SetFileName = 15;
	private const int SetTitle = 17;
	private const int GetResult = 20;
	private const int SetDefaultExtension = 22;

	private const int GetDisplayName = 5;

	#endregion

	private const uint FileSystemPath = 0x80058000;

	public static string? OpenFile(nint owner) =>
		Run(owner, FileOpenDialogClass, FileMustExist | PathMustExist, "Open File", null);

	public static string? OpenFolder(nint owner) =>
		Run(owner, FileOpenDialogClass, PickFolders | PathMustExist, "Open Folder", null);

	public static string? SaveFile(nint owner, string? suggestedName) =>
		Run(owner, FileSaveDialogClass, OverwritePrompt | PathMustExist, "Save As", suggestedName);

	private static string? Run(nint owner, Guid dialogClass, uint options, string title, string? fileName) {
		Guid clsid = dialogClass;
		Guid iid = FileDialogInterface;

		if (CoCreateInstance(&clsid, 0, InProcServer, &iid, out nint dialog) < 0 || dialog == 0) return null;

		try {
			void** vtable = *(void***)dialog;

			uint defaults = 0;
			((delegate* unmanaged<nint, uint*, int>)vtable[GetOptions])(dialog, &defaults);

			((delegate* unmanaged<nint, uint, int>)vtable[SetOptions])(
				dialog, defaults | options | ForceFileSystem);

			fixed (char* titleText = title) {
				((delegate* unmanaged<nint, char*, int>)vtable[SetTitle])(dialog, titleText);
			}

			if (fileName is { Length: > 0 }) {
				fixed (char* name = fileName) ((delegate* unmanaged<nint, char*, int>)vtable[SetFileName])(dialog, name);

				if (Path.GetExtension(fileName) is { Length: > 1 } extension) {
					fixed (char* defaultExtension = extension.AsSpan(1)) {
						((delegate* unmanaged<nint, char*, int>)vtable[SetDefaultExtension])(
							dialog, defaultExtension);
					}
				}
			}

			if (((delegate* unmanaged<nint, nint, int>)vtable[Show])(dialog, owner) < 0) return null;

			nint item = 0;

			if (((delegate* unmanaged<nint, nint*, int>)vtable[GetResult])(dialog, &item) < 0 || item == 0) {
				return null;
			}

			try {
				return PathOf(item);
			}
			finally {
				ReleaseObject(item);
			}
		}
		finally {
			ReleaseObject(dialog);
		}
	}

	private static string? PathOf(nint item) {
		void** vtable = *(void***)item;

		char* buffer = null;

		if (((delegate* unmanaged<nint, uint, char**, int>)vtable[GetDisplayName])(
			item, FileSystemPath, &buffer) < 0 || buffer is null) return null;

		try {
			return new string(buffer);
		}
		finally {
			CoTaskMemFree((nint)buffer);
		}
	}

	private static void ReleaseObject(nint instance) {
		void** vtable = *(void***)instance;
		((delegate* unmanaged<nint, uint>)vtable[Release])(instance);
	}

	[LibraryImport(Ole32)]
	private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid,
		out nint instance);

	[LibraryImport(Ole32)]
	private static partial void CoTaskMemFree(nint pointer);
}
