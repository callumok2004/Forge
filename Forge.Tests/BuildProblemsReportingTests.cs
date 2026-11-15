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
	public async Task StreamingBuildUpdatesAreBatched() {
		(DiagnosticsCoordinator coordinator, ICommandHost host, RecordingHost recorder) = Create();
		List<Diagnostic> growing = [];

		for (int i = 1; i <= 2000; i++) {
			growing.Add(new Diagnostic($@"C:\src\File{i % 40}.cs", i, 1, $"warning {i}", DiagnosticSeverity.Warning, "CS0168"));
			coordinator.SetBuild(host, [.. growing], final: false);
		}

		await Task.Delay(600);

		int streamed;

		lock (recorder.Reports) streamed = recorder.Reports.Count(static report => report.Owner == MsBuildProblemMatcher.Owner);

		Assert.InRange(streamed, 1, 10);
		Assert.Equal(2000, recorder.LastBuild.Count);

		coordinator.SetBuild(host, [.. growing]);
		Assert.Equal(2000, recorder.LastBuild.Count);
	}

	[Fact]
	public void MatcherSignalsOnlyNewDiagnostics() {
		MsBuildProblemMatcher matcher = new();
		const string Line = @"C:\src\A.cs(1,1): warning CS0168: unused";

		Assert.True(matcher.Read(Line));
		Assert.False(matcher.Read(Line));
		Assert.False(matcher.Read("Build succeeded."));
		Assert.Single(matcher.Diagnostics);
	}

	private const string Full = @"C:\src\A.cs(5,9): warning CS8600: Converting null literal or possible null value to non-nullable type.";
	private const string Clipped = @"C:\src\A.cs(5,9): warning CS8600: Converting null literal or";

	[Fact]
	public void ClippedLineThenFullKeepsFull() {
		MsBuildProblemMatcher matcher = new();

		Assert.True(matcher.Read(Clipped));
		Assert.True(matcher.Read(Full));

		Assert.Equal("Converting null literal or possible null value to non-nullable type.", Assert.Single(matcher.Diagnostics).Message);
		Assert.Equal(1, matcher.WarningCount);
	}

	[Fact]
	public void FullLineThenClippedKeepsFull() {
		MsBuildProblemMatcher matcher = new();

		Assert.True(matcher.Read(Full));
		Assert.False(matcher.Read(Clipped));

		Assert.Equal("Converting null literal or possible null value to non-nullable type.", Assert.Single(matcher.Diagnostics).Message);
		Assert.Equal(1, matcher.WarningCount);
	}

	[Fact]
	public void DifferentMessagesAtSameLocationStaySeparate() {
		MsBuildProblemMatcher matcher = new();

		matcher.Read(@"C:\src\A.cs(5,9): warning CS8604: Possible null reference argument for parameter 'a' in 'void M(string a, string b)'.");
		matcher.Read(@"C:\src\A.cs(5,9): warning CS8604: Possible null reference argument for parameter 'b' in 'void M(string a, string b)'.");

		Assert.Equal(2, matcher.Diagnostics.Count);
	}

	[Fact]
	public void ClippedLinesInterleavedWithManyWarnings() {
		MsBuildProblemMatcher matcher = new();

		for (int i = 1; i <= 500; i++) {
			string full = $@"C:\src\File{i}.cs({i},3): warning CS8604: Possible null reference argument for parameter 'pMesh' in 'void MeshBuilder.Begin(Mesh pMesh)'.";

			matcher.Read(full[..Math.Min(full.Length, 60 + (i % 30))]);
			matcher.Read(full);
			matcher.Read(full[..Math.Min(full.Length, 70)]);
		}

		Assert.Equal(500, matcher.Diagnostics.Count);
		Assert.Equal(500, matcher.WarningCount);
		Assert.All(matcher.Diagnostics, static diagnostic => Assert.EndsWith("(Mesh pMesh)'.", diagnostic.Message, StringComparison.Ordinal));
	}

	[Fact]
	public void ManyBuildDiagnosticsAllReported() {
		(DiagnosticsCoordinator coordinator, ICommandHost host, RecordingHost recorder) = Create();
		Diagnostic[] many = [.. Enumerable.Range(1, 1809).Select(static i => new Diagnostic($@"C:\src\File{i % 40}.cs", i, 1, $"warning {i}", DiagnosticSeverity.Warning, "CS0168"))];

		coordinator.SetBuild(host, many);

		Assert.Equal(1809, recorder.LastBuild.Count);
	}
}
