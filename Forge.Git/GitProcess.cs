using System.Diagnostics;
using System.Text;

using Forge.Core.Native;

namespace Forge.Git;

public static class GitProcess
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	public static bool IsAvailable => _available ??= Run(Environment.CurrentDirectory, "--version") is not null;

	private static bool? _available;

	public static string? Run(string workingDirectory, params string[] arguments) {
		ProcessStartInfo start = new() {
			FileName = "git",
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
		};

		start.ArgumentList.Add("--no-pager");
		start.ArgumentList.Add("-c");
		start.ArgumentList.Add("core.quotepath=false");

		foreach (string argument in arguments) start.ArgumentList.Add(argument);

		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

		try {
			using Process? process = ProcessGroup.Start(start);

			if (process is null) return null;

			string output = process.StandardOutput.ReadToEnd();

			if (!process.WaitForExit(Timeout)) {
				process.Kill(entireProcessTree: true);
				return null;
			}

			return process.ExitCode == 0 ? output : null;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
			or InvalidOperationException or IOException) {
			return null;
		}
	}

	public static string? FindRepositoryRoot(string path) {
		string? output = Run(path, "rev-parse", "--show-toplevel");

		if (output is null) return null;

		string root = output.Trim();
		return root.Length == 0 ? null : Path.GetFullPath(root);
	}
}
