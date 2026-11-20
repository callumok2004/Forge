using Forge.Debug;

namespace Forge.Extensions.Dotnet.Debugger;

public static class DebugAdapterRegistration
{
	private static readonly (string Type, string Label)[] Types = [("coreclr", ".NET Core"), ("clr", ".NET Framework"), ("dotnet", ".NET")];

	private static readonly Dictionary<string, string> ConsoleDefaults = new() { ["console"] = "internalConsole" };

	private static string Executable => Path.Combine(AppContext.BaseDirectory, "Debugger", "dncdbg", "dncdbg.exe");

	public static void Register() {
		string[] arguments = Arguments();

		foreach ((string type, string label) in Types)
			DebugAdapters.Register(new DebugAdapterDescriptor(type, label, Executable, arguments) { LaunchDefaults = ConsoleDefaults });
	}

#if FORGE_DEBUG
	private static string[] Arguments() {
		string directory = Path.Combine(Path.GetTempPath(), "forge-dncdbg");
		Directory.CreateDirectory(directory);

		foreach (FileInfo old in new DirectoryInfo(directory).GetFiles("*.log").OrderByDescending(static file => file.LastWriteTimeUtc).Skip(20)) {
			try {
				old.Delete();
			}
			catch (IOException) {
			}
		}

		string name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";

		return ["--interpreter=vscode", $"--log={Path.Combine(directory, name + ".log")}", $"--logProtocol={Path.Combine(directory, name + ".protocol.log")}"];
	}
#else
	private static string[] Arguments() => ["--interpreter=vscode"];
#endif
}
