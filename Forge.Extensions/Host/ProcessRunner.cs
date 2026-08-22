using System.Diagnostics;

using Forge.Common.Render;
using Forge.Core.Native;

namespace Forge.Extensions.Host;

public static class ProcessRunner
{
	public static void Run(string fileName, string arguments, string? workingDirectory,
		Action<string> log, Action<int>? finished = null, AmbientSignal? signal = null) {
		if (signal is { } starting) Ambient.Begin(starting);

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
					if (signal is { } unavailable) Ambient.End(unavailable);

					log($"Could not start {fileName}.");
					finished?.Invoke(-1);
					return;
				}

				ProcessGroup.Adopt(process);

				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				process.WaitForExit();

				if (signal is { } completed) Ambient.End(completed);

				finished?.Invoke(process.ExitCode);
			}
			catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
				or InvalidOperationException or IOException) {
				if (signal is { } failed) Ambient.End(failed);

				log($"{fileName} could not be run: {exception.Message}");
				finished?.Invoke(-1);
			}
		}, null);
	}
}
