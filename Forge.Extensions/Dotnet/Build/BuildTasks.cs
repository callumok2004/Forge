using System.Diagnostics;

using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Extensions.Dotnet.Build;

public sealed class BuildTasks(SolutionWorkspace workspace, DotnetOutput output)
{
	private const string BuildLoggerArguments = "-consoleLoggerParameters:NoSummary;ForceNoAlign";

	public Action? Started { get; init; }

	public Action? Finished { get; init; }

	public void Run(ICommandHost host, string command, string? project = null) {
		if (project is null && workspace.Target is null) workspace.Reload(host.GetWorkspacePath());

		if ((project ?? workspace.Target) is not { } target) {
			output.Log(host, "no solution or project in this workspace.");
			host.Refresh();
			return;
		}

		string arguments = command == "build" ? $"{command} \"{target}\" {BuildLoggerArguments}" : $"{command} \"{target}\"";

		output.Log(host, $"{command} {Path.GetFileName(target)}");

		MsBuildProblemMatcher matcher = new();
		long started = Stopwatch.GetTimestamp();
		AmbientSignal signal = command == "test" ? AmbientSignal.Tests : AmbientSignal.Build;

		Ambient.Begin(signal);

		if (command == "build") Started?.Invoke();

		host.RunInTerminal($"dotnet {command}", $"dotnet {arguments}", Path.GetDirectoryName(target), line => {
			if (matcher.Read(line)) host.ReportProblems(matcher.Diagnostics);

			host.Refresh();
		}, exitCode => {
			Ambient.End(signal, exitCode == 0);

			if (command == "build") Finished?.Invoke();

			TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

			output.Log(host, exitCode == 0 ? $".NET: {command} succeeded in {elapsed.TotalSeconds:F1}s" : $".NET: {command} failed with exit code {exitCode} after {elapsed.TotalSeconds:F1}s");

			if (matcher.Diagnostics.Count > 0) {
				output.Log(host, $"{matcher.ErrorCount} error(s), {matcher.WarningCount} warning(s)");

				if (exitCode != 0) host.ShowPanel("Problems");
			}
			else if (exitCode == 0) host.ReportProblems([]);

			host.Refresh();
		});
	}

	public void RunForActiveProject(ICommandHost host, string command) => Run(host, command, host.GetActiveFilePath() is { } active ? ProjectDiscovery.ProjectOf(active) : null);
}
