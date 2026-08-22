namespace Forge.Core;

public static class UserData
{
	private static string? _directory;

	public static string Directory => _directory ??= Resolve();

	public static string SettingsPath => Path.Combine(Directory, "settings.json");

	public static string WindowStatePath => Path.Combine(Directory, "window.json");

	public static string TerminalHistoryPath => Path.Combine(Directory, "terminal-history.json");

	public static string SourceControlStatePath => Path.Combine(Directory, "source-control.txt");

	public static string BreakpointsPath => Path.Combine(Directory, "breakpoints.json");

	public static string WorkspacesDirectory => Path.Combine(Directory, "workspaces");

	public static string DebuggersPath => Path.Combine(Directory, "debuggers");

	public static string ExtensionsDirectory => Path.Combine(Directory, "extensions");
	public static string ExtensionStorageDirectory => Path.Combine(Directory, "extension-storage");


	private static string Resolve() {
		string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

		if (roaming.Length == 0) return AppContext.BaseDirectory;

		string forge = Path.Combine(roaming, "Forge");

		try {
			System.IO.Directory.CreateDirectory(forge);
			return forge;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return AppContext.BaseDirectory;
		}
	}
}
