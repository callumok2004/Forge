using System.Diagnostics;
using System.Text;

using Forge.Core.Native;

namespace Forge.Git;

public static class GitProcess
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	public static bool IsAvailable => _available ??= Run(Environment.CurrentDirectory, "--version") is not null;

	private static bool? _available;

	public static Action<string>? Log { get; set; }

	private static void Report(string[] arguments, long started, int exitCode, string error) {
		if (Log is not { } log) return;

		string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
		log(stamp + " [info] > git " + string.Join(' ', arguments) + " [" + (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds + "ms]");

		if (exitCode == 0 || error.Length == 0) return;

		foreach (string line in error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) log(stamp + " [info] " + line);
	}

	public static string? RunWithInput(string workingDirectory, string input, params string[] arguments) {
		ProcessStartInfo start = new() {
			FileName = "git",
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardInputEncoding = new UTF8Encoding(false),
		};

		start.ArgumentList.Add("--no-pager");

		foreach (string argument in arguments) start.ArgumentList.Add(argument);

		start.Environment["GIT_TERMINAL_PROMPT"] = "0";

		try {
			using Process? process = ProcessGroup.Start(start);

			if (process is null) return null;

			long started = Stopwatch.GetTimestamp();
			Task<string> error = process.StandardError.ReadToEndAsync();
			process.StandardInput.Write(input);
			process.StandardInput.Close();

			string output = process.StandardOutput.ReadToEnd();

			if (!process.WaitForExit(Timeout)) {
				process.Kill(entireProcessTree: true);
				return null;
			}

			Report(arguments, started, process.ExitCode, error.Result);
			return process.ExitCode == 0 ? output : null;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
			return null;
		}
	}

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

			long started = Stopwatch.GetTimestamp();
			Task<string> error = process.StandardError.ReadToEndAsync();
			string output = process.StandardOutput.ReadToEnd();

			if (!process.WaitForExit(Timeout)) {
				process.Kill(entireProcessTree: true);
				return null;
			}

			Report(arguments, started, process.ExitCode, error.Result);
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
