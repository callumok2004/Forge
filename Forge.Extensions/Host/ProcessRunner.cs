using System.Diagnostics;

using Forge.Core.Native;

namespace Forge.Extensions.Host;

public static class ProcessRunner
{
	public static void Run(string fileName, string arguments, string? workingDirectory,
		Action<string> log, Action<int>? finished = null) {
		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			try {
				ProcessStartInfo info = new() {
					FileName = fileName,
					Arguments = arguments,
					WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				};

				info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
				info.Environment["DOTNET_NOLOGO"] = "1";
				info.Environment["TERM"] = "dumb";

				using Process process = new() { StartInfo = info };

				process.OutputDataReceived += (_, e) => {
					if (e.Data is { Length: > 0 }) log(e.Data);
				};

				process.ErrorDataReceived += (_, e) => {
					if (e.Data is { Length: > 0 }) log(e.Data);
				};

				if (!process.Start()) {
					log($"Could not start {fileName}.");
					finished?.Invoke(-1);
					return;
				}

				ProcessGroup.Adopt(process);

				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				process.WaitForExit();

				finished?.Invoke(process.ExitCode);
			}
			catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
				or InvalidOperationException or IOException) {
				log($"{fileName} could not be run: {exception.Message}");
				finished?.Invoke(-1);
			}
		}, null);
	}
}
