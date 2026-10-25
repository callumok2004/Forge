using Forge.Debug;

namespace Forge.Extensions.Dotnet.Debugger;

public static class DebugAdapterRegistration
{
	private static readonly (string Type, string Label)[] Types = [("coreclr", ".NET Core"), ("clr", ".NET Framework"), ("dotnet", ".NET")];

	public static void Register(string storageDirectory) {
		string executable = NetCoreDbgAcquisition.ExecutablePath(storageDirectory);

		foreach ((string type, string label) in Types) {
			DebugAdapters.Register(new DebugAdapterDescriptor(type, label, executable, ["--interpreter=vscode"]) {
				Acquire = progress => NetCoreDbgAcquisition.AcquireAsync(storageDirectory, progress),
			});
		}
	}
}
