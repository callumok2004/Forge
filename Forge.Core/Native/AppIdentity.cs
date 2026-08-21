using System.Runtime.InteropServices;

namespace Forge.Core.Native;

public static partial class AppIdentity
{
	public const string ModelId = "Forge.Editor";

	public static bool Applied { get; private set; }

	public static void Apply() {
		if (Applied) return;

		try {
			Applied = SetCurrentProcessExplicitAppUserModelID(ModelId) == 0;
		}
		catch (DllNotFoundException) {
		}
		catch (EntryPointNotFoundException) {
		}
	}

	[LibraryImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID", StringMarshalling = StringMarshalling.Utf16)]
	private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
