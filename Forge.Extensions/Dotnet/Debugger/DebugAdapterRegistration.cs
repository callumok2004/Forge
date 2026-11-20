using Forge.Debug;

namespace Forge.Extensions.Dotnet.Debugger;

public static class DebugAdapterRegistration
{
	private static readonly (string Type, string Label)[] Types = [("coreclr", ".NET Core"), ("clr", ".NET Framework"), ("dotnet", ".NET")];

	private static readonly Dictionary<string, string> ConsoleDefaults = new() { ["console"] = "internalConsole" };

	private static string Executable => Path.Combine(AppContext.BaseDirectory, "Debugger", "dncdbg", "dncdbg.exe");

	public static void Register() {
		foreach ((string type, string label) in Types)
			DebugAdapters.Register(new DebugAdapterDescriptor(type, label, Executable, ["--interpreter=vscode"]) { LaunchDefaults = ConsoleDefaults });
	}
}
