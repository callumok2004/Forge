namespace Forge.Terminal;

public readonly record struct TerminalProfile(string Name, string Path, string Arguments)
{
	public string CommandLine =>
		Arguments is { Length: > 0 } ? $"\"{Path}\" {Arguments}" : $"\"{Path}\"";
}

public static class TerminalProfiles
{
	private const string PowerShellArguments =
		"-NoLogo -NoExit -Command \"try { Set-PSReadLineOption -PredictionSource None } catch { }\"";

	public static IReadOnlyList<TerminalProfile> Detect() {
		List<TerminalProfile> profiles = [];

		string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

		Add(profiles, "PowerShell", Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
			PowerShellArguments);

		Add(profiles, "PowerShell",
			Path.Combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"), PowerShellArguments);

		Add(profiles, "Windows PowerShell",
			Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), PowerShellArguments);

		Add(profiles, "Git Bash", Path.Combine(programFiles, "Git", "bin", "bash.exe"), "--login -i");

		Add(profiles, "Command Prompt", Path.Combine(system, "cmd.exe"), string.Empty);

		if (profiles.Count == 0) profiles.Add(new TerminalProfile("Command Prompt", "cmd.exe", string.Empty));

		return profiles;
	}

	public static TerminalProfile Default(IReadOnlyList<TerminalProfile> profiles, string? preferred) {
		if (preferred is { Length: > 0 }) {
			foreach (TerminalProfile profile in profiles) {
				if (string.Equals(profile.Name, preferred, StringComparison.OrdinalIgnoreCase)) return profile;
			}
		}

		return profiles[0];
	}

	private static void Add(List<TerminalProfile> profiles, string name, string path, string arguments) {
		if (!File.Exists(path)) return;

		foreach (TerminalProfile existing in profiles) {
			if (existing.Name == name) return;
		}

		profiles.Add(new TerminalProfile(name, path, arguments));
	}
}
