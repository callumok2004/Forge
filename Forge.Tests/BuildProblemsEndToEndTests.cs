using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Build;
using Forge.Terminal;

namespace Forge.Tests;

public sealed class BuildProblemsEndToEndTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-build-e2e", Guid.NewGuid().ToString("N"));

	public BuildProblemsEndToEndTests() {
		Directory.CreateDirectory(_root);
		File.WriteAllText(Path.Combine(_root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Library</OutputType><Nullable>enable</Nullable></PropertyGroup></Project>");
		File.WriteAllText(Path.Combine(_root, "Sample.cs"), "namespace Sample;\n\npublic static class Thing\n{\n\tpublic static void Run() {\n\t\tint unused;\n\t}\n}\n");
	}

	public void Dispose() {
		try {
			Directory.Delete(_root, true);
		}
		catch (IOException) {
		}
		catch (UnauthorizedAccessException) {
		}
	}

	private async Task<(List<string> Lines, MsBuildProblemMatcher Matcher, int ExitCode)> BuildAsync(int columns) {
		List<string> lines = [];
		MsBuildProblemMatcher matcher = new();
		TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using TerminalSession session = new() {
			LineObserver = line => {
				lock (lines) lines.Add(line);

				matcher.Read(line);
			},
			ExitObserver = code => exited.TrySetResult(code),
		};

		string project = Path.Combine(_root, "Sample.csproj");
		string command = $"dotnet build \"{project}\" {BuildTasks.LoggerArguments}";
		TerminalProfile profile = new("build", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/c {command}");

		Assert.True(session.Start(profile, _root, columns, 30));

		int exitCode = await exited.Task.WaitAsync(TimeSpan.FromMinutes(3));
		return (lines, matcher, exitCode);
	}

	[Fact]
	public async Task BuildWarningReachesMatcherThroughTerminal() {
		(List<string> lines, MsBuildProblemMatcher matcher, int exitCode) = await BuildAsync(120);

		Assert.Equal(0, exitCode);
		Diagnostic warning = Assert.Single(matcher.Diagnostics, static diagnostic => diagnostic.Code == "CS0168");
		Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
		Assert.Equal(6, warning.Line);
		Assert.EndsWith("Sample.cs", warning.File, StringComparison.OrdinalIgnoreCase);
		Assert.True(Path.IsPathRooted(warning.File), string.Join("\n", lines));
	}

	[Fact]
	public async Task BuildWarningSurvivesNarrowTerminal() {
		(List<string> lines, MsBuildProblemMatcher matcher, _) = await BuildAsync(40);

		Assert.True(matcher.Diagnostics.Any(static diagnostic => diagnostic.Code == "CS0168"), string.Join("\n", lines));
	}
}
