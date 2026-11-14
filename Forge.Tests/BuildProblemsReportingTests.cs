using System.Reflection;

using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Build;
using Forge.Extensions.Dotnet.Language;
using Forge.Extensions.Dotnet.Server;

namespace Forge.Tests;

public class BuildProblemsReportingTests
{
	public class RecordingHost : DispatchProxy
	{
		public List<(string Owner, bool Build, IReadOnlyList<Diagnostic> Problems)> Reports { get; } = [];

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
			if (targetMethod?.Name == nameof(ICommandHost.ReportProblems)) {
				lock (Reports) Reports.Add(((string)args![0]!, (bool)args[1]!, [.. (IReadOnlyList<Diagnostic>)args[2]!]));
			}

			return targetMethod?.ReturnType is { IsValueType: true } type && type != typeof(void) ? Activator.CreateInstance(type) : null;
		}

		public IReadOnlyList<Diagnostic> LastBuild {
			get {
				lock (Reports) return Reports.LastOrDefault(static report => report.Owner == MsBuildProblemMatcher.Owner).Problems ?? [];
			}
		}
	}

	private static (DiagnosticsCoordinator Coordinator, ICommandHost Host, RecordingHost Recorder) Create() {
		DiagnosticsCoordinator coordinator = new(new RoslynLanguageClient());
		ICommandHost host = DispatchProxy.Create<ICommandHost, RecordingHost>();
		coordinator.Attach(host);
		return (coordinator, host, (RecordingHost)(object)host);
	}

	private static void PublishWorkspace(DiagnosticsCoordinator coordinator, string file, IReadOnlyList<ServerDiagnostic> diagnostics) =>
		typeof(DiagnosticsCoordinator).GetMethod("OnWorkspacePublished", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(coordinator, [file, diagnostics]);

	private const string File = @"C:\src\Program.cs";

	private static readonly Diagnostic Warning = new(File, 48, 7, "Field is never assigned", DiagnosticSeverity.Warning, "CS0649");

	[Fact]
	public void BuildDiagnosticsAreReportedAsBuild() {
		(DiagnosticsCoordinator coordinator, ICommandHost host, RecordingHost recorder) = Create();

		coordinator.SetBuild(host, [Warning]);

		Diagnostic reported = Assert.Single(recorder.LastBuild);
		Assert.Equal("CS0649", reported.Code);
		Assert.False(reported.Superseded);
		Assert.Contains(recorder.Reports, static report => report.Owner == MsBuildProblemMatcher.Owner && report.Build);
	}

	[Fact]
	public void BuildDiagnosticCoveredByIntelliSenseIsKeptButMarked() {
		(DiagnosticsCoordinator coordinator, ICommandHost host, RecordingHost recorder) = Create();

		PublishWorkspace(coordinator, File, [new ServerDiagnostic(File, 48, 7, "Field is never assigned", DiagnosticSeverity.Warning, "CS0649")]);
		coordinator.SetBuild(host, [Warning]);

		Diagnostic reported = Assert.Single(recorder.LastBuild);
		Assert.True(reported.Superseded);
	}

	[Fact]
	public void ClearingBuildEmptiesBuildList() {
		(DiagnosticsCoordinator coordinator, ICommandHost host, RecordingHost recorder) = Create();

		coordinator.SetBuild(host, [Warning]);
		coordinator.SetBuild(host, []);

		Assert.Empty(recorder.LastBuild);
	}

	[Fact]
	public void ManyBuildDiagnosticsAllReported() {
		(DiagnosticsCoordinator coordinator, ICommandHost host, RecordingHost recorder) = Create();
		Diagnostic[] many = [.. Enumerable.Range(1, 1809).Select(static i => new Diagnostic($@"C:\src\File{i % 40}.cs", i, 1, $"warning {i}", DiagnosticSeverity.Warning, "CS0168"))];

		coordinator.SetBuild(host, many);

		Assert.Equal(1809, recorder.LastBuild.Count);
	}
}
