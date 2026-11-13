using System.Diagnostics;
using System.Text.RegularExpressions;

using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Extensions.Dotnet.Language;

public sealed partial class ProjectDefines(SolutionWorkspace workspace, DotnetSettings settings)
{
	private readonly Dictionary<string, Task<IReadOnlySet<string>>> _defines = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	[GeneratedRegex(@"\.(cs|fs|vb)proj$", RegexOptions.IgnoreCase)]
	private static partial Regex ProjectFile();

	[GeneratedRegex(@"\s*\(([^)]*)\)\s*$")]
	private static partial Regex FrameworkSuffix();

	public void Invalidate() {
		lock (_sync) _defines.Clear();
	}

	public Task<IReadOnlySet<string>> For(ProjectContext context) {
		if (ProjectPathFor(context) is not { } path) return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

		Match suffix = FrameworkSuffix().Match(context.Label);
		string? framework = suffix.Success && suffix.Groups[1].Value.StartsWith("net", StringComparison.OrdinalIgnoreCase) ? suffix.Groups[1].Value.Trim() : null;
		string key = framework is null ? path : $"{path}|{framework}";

		lock (_sync) {
			if (!_defines.TryGetValue(key, out Task<IReadOnlySet<string>>? defines)) _defines[key] = defines = EvaluateAsync(path, framework);

			return defines;
		}
	}

	private string? ProjectPathFor(ProjectContext context) {
		string debugName = context.Id[(context.Id.IndexOf('|') + 1)..];
		string candidate = FrameworkSuffix().Replace(debugName, string.Empty).Trim();

		if (ProjectFile().IsMatch(candidate) && File.Exists(candidate)) return candidate;

		string name = FrameworkSuffix().Replace(context.Label, string.Empty).Trim();

		foreach (string project in workspace.Projects) {
			if (string.Equals(Path.GetFileNameWithoutExtension(project), name, StringComparison.OrdinalIgnoreCase)) return project;
		}

		return null;
	}

	private async Task<IReadOnlySet<string>> EvaluateAsync(string path, string? framework) {
		ProcessStartInfo start = new(LanguageServerLocator.DotnetPath(settings.DotnetPath)) {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
		};

		start.ArgumentList.Add("msbuild");
		start.ArgumentList.Add(path);
		start.ArgumentList.Add("-nologo");
		start.ArgumentList.Add("-property:Configuration=Debug");
		start.ArgumentList.Add("-getProperty:DefineConstants");

		if (framework is not null) start.ArgumentList.Add($"-property:TargetFramework={framework}");

		try {
			using Process process = Process.Start(start)!;
			Task<string> error = process.StandardError.ReadToEndAsync();
			string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
			await error.ConfigureAwait(false);
			await process.WaitForExitAsync().ConfigureAwait(false);

			return process.ExitCode == 0 ? Preprocessor.ParseDefineConstants(output.Trim()) : new HashSet<string>();
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
			return new HashSet<string>();
		}
	}
}
