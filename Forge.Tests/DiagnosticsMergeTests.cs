using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.Dotnet.Build;
using Forge.Extensions.Dotnet.Language;
using Forge.Extensions.Dotnet.Server;

namespace Forge.Tests;

public class DiagnosticsMergeTests
{
	private const string Open = @"C:\src\Open.cs";
	private const string Closed = @"C:\src\Closed.cs";
	private const string Unreported = @"C:\src\Unreported.cs";

	private static readonly HashSet<string> BuildOnly = [with(StringComparer.OrdinalIgnoreCase), "CS8784"];

	private static bool Keep(string file, string code, bool compilerFull = true, bool analyzerFull = false) => DiagnosticsCoordinator.KeepBuild(new Diagnostic(file, 1, 1, "message", DiagnosticSeverity.Error, code), BuildOnly, path => path.Equals(Open, StringComparison.OrdinalIgnoreCase), path => path.Equals(Closed, StringComparison.OrdinalIgnoreCase), compilerFull, analyzerFull);

	[Fact]
	public void BuildOnlyIdsAlwaysKept() {
		Assert.True(Keep(Open, "CS8784"));
		Assert.True(Keep(Closed, "CS8784"));
	}

	[Fact]
	public void LiveDocumentReplacesBuildDiagnostics() {
		Assert.False(Keep(Open, "CS0103"));
		Assert.False(Keep(Open, "IDE0005"));
		Assert.False(Keep(@"c:\SRC\open.cs", "CS0103"));
	}

	[Fact]
	public void ClosedFileCompilerErrorsComeFromWorkspaceWhenFullSolution() {
		Assert.False(Keep(Closed, "CS0103"));
		Assert.True(Keep(Closed, "CS0103", compilerFull: false));
	}

	[Fact]
	public void ClosedFileAnalyzerWarningsKeptUnlessAnalyzersFullSolution() {
		Assert.True(Keep(Closed, "CA1822"));
		Assert.False(Keep(Closed, "CA1822", analyzerFull: true));
	}

	[Fact]
	public void BuildKeptUntilLiveAnalysisReportsFile() => Assert.True(Keep(Unreported, "CS0103"));

	[Fact]
	public void MergePrefersDocumentPullOverWorkspace() {
		Dictionary<string, IReadOnlyList<ServerDiagnostic>> documents = new(StringComparer.OrdinalIgnoreCase) { [Open] = [new ServerDiagnostic(Open, 1, 1, "live", DiagnosticSeverity.Error, "CS1")] };
		Dictionary<string, IReadOnlyList<ServerDiagnostic>> workspace = new(StringComparer.OrdinalIgnoreCase) {
			[Open] = [new ServerDiagnostic(Open, 1, 1, "stale", DiagnosticSeverity.Error, "CS1")],
			[Closed] = [new ServerDiagnostic(Closed, 2, 1, "closed", DiagnosticSeverity.Warning, "CS2")],
			[Unreported] = [new ServerDiagnostic(Unreported, 3, 1, "opening", DiagnosticSeverity.Warning, "CS3")],
		};

		Diagnostic[] merged = DiagnosticsCoordinator.Merge(documents, workspace, path => path == Open || path == Unreported);

		Assert.Equal(["live", "closed"], merged.Select(static diagnostic => diagnostic.Message).Order().Reverse());
	}

	[Fact]
	public void MergeKeepsEmptyReportsOut() {
		Dictionary<string, IReadOnlyList<ServerDiagnostic>> documents = new(StringComparer.OrdinalIgnoreCase) { [Open] = [] };
		Dictionary<string, IReadOnlyList<ServerDiagnostic>> workspace = new(StringComparer.OrdinalIgnoreCase) { [Closed] = [] };

		Assert.Empty(DiagnosticsCoordinator.Merge(documents, workspace, static _ => false));
	}

	[Fact]
	public void MatcherParsesFullRangeLocation() {
		Diagnostic? diagnostic = MsBuildProblemMatcher.Parse(@"C:\src\A.cs(12,5,12,9): error CS0103: The name 'x' does not exist [C:\src\A.csproj]");

		Assert.NotNull(diagnostic);
		Assert.Equal(12, diagnostic.Value.Line);
		Assert.Equal(5, diagnostic.Value.Column);
		Assert.Equal("CS0103", diagnostic.Value.Code);
		Assert.Equal("The name 'x' does not exist", diagnostic.Value.Message);
	}

	[Fact]
	public void MatcherDeduplicatesAcrossTargetFrameworks() {
		MsBuildProblemMatcher matcher = new();

		matcher.Read(@"C:\src\A.cs(1,1): warning CS0168: unused [C:\src\A.csproj::TargetFramework=net10.0]");
		matcher.Read(@"C:\src\A.cs(1,1): warning CS0168: unused [C:\src\A.csproj::TargetFramework=net11.0]");

		Assert.Single(matcher.Diagnostics);
		Assert.Equal(1, matcher.WarningCount);
	}
}
