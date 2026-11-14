using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Server.Features;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Tests;

public sealed class SharedSourceProjectsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-shared-tests", Guid.NewGuid().ToString("N"));

	public SharedSourceProjectsTests() => Directory.CreateDirectory(_root);

	public void Dispose() {
		try {
			Directory.Delete(_root, true);
		}
		catch (IOException) {
		}
	}

	private string Project(string name, string body = "") {
		string directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, name + ".csproj");
		File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{body}</ItemGroup></Project>");
		return path;
	}

	private string Solution(string body) {
		string path = Path.Combine(_root, "Test.slnx");
		File.WriteAllText(path, $"<Solution>{body}</Solution>");
		return path;
	}

	[Fact]
	public void SkipsBuildDisabledProjectWhoseSourceIsLinkedElsewhere() {
		Project("Shared");
		Project("Client", @"<Compile Include=""..\Shared\*.cs"" Link=""Shared\%(Filename)%(Extension)"" />");
		Project("Server", @"<Compile Include=""..\Shared\*.cs"" />");
		string solution = Solution(@"<Project Path=""Client/Client.csproj"" /><Project Path=""Server/Server.csproj"" /><Project Path=""Shared/Shared.csproj""><Build Project=""false"" /></Project>");

		IReadOnlyList<string>? open = SharedSourceProjects.ProjectsToOpen(solution, out IReadOnlyList<string> skipped);

		Assert.NotNull(open);
		Assert.Equal(2, open.Count);
		Assert.DoesNotContain(open, path => path.EndsWith("Shared.csproj", StringComparison.Ordinal));
		Assert.Single(skipped);
	}

	[Fact]
	public void KeepsBuildDisabledProjectNobodyLinks() {
		Project("Tool");
		Project("App");
		string solution = Solution(@"<Project Path=""App/App.csproj"" /><Project Path=""Tool/Tool.csproj""><Build Project=""false"" /></Project>");

		Assert.Null(SharedSourceProjects.ProjectsToOpen(solution, out IReadOnlyList<string> skipped));
		Assert.Empty(skipped);
	}

	[Fact]
	public void KeepsEverythingWhenAllBuild() {
		Project("Shared");
		Project("Client", @"<Compile Include=""..\Shared\*.cs"" />");
		string solution = Solution(@"<Project Path=""Client/Client.csproj"" /><Project Path=""Shared/Shared.csproj"" />");

		Assert.Null(SharedSourceProjects.ProjectsToOpen(solution, out _));
	}

	[Fact]
	public void ConfigurationSpecificBuildFlagIsIgnored() {
		Project("Shared");
		Project("Client", @"<Compile Include=""..\Shared\*.cs"" />");
		string solution = Solution(@"<Project Path=""Client/Client.csproj"" /><Project Path=""Shared/Shared.csproj""><Build Solution=""Release|*"" Project=""false"" /></Project>");

		Assert.Null(SharedSourceProjects.ProjectsToOpen(solution, out _));
	}

	[Fact]
	public void LinkFromAnotherDisabledProjectDoesNotCount() {
		Project("Shared");
		Project("Other", @"<Compile Include=""..\Shared\*.cs"" />");
		string solution = Solution(@"<Project Path=""Other/Other.csproj""><Build Project=""false"" /></Project><Project Path=""Shared/Shared.csproj""><Build Project=""false"" /></Project>");

		Assert.Null(SharedSourceProjects.ProjectsToOpen(solution, out _));
	}

	[Fact]
	public void SlnSolutionsAreUntouched() {
		string path = Path.Combine(_root, "Test.sln");
		File.WriteAllText(path, string.Empty);

		Assert.Null(SharedSourceProjects.ProjectsToOpen(path, out _));
	}

	[Fact]
	public void MissingSolutionIsUntouched() => Assert.Null(SharedSourceProjects.ProjectsToOpen(Path.Combine(_root, "Missing.slnx"), out _));

	[Fact]
	public void MalformedSolutionIsUntouched() => Assert.Null(SharedSourceProjects.ProjectsToOpen(Solution("<Project"), out _));

	[Theory]
	[InlineData(@"..\Shared\*.cs", true)]
	[InlineData(@"..\Shared\Sub\File.cs", true)]
	[InlineData(@"../Shared/**/*.cs", true)]
	[InlineData(@"..\SharedOther\*.cs", false)]
	[InlineData(@"Local.cs", false)]
	[InlineData(@"$(SharedDir)\*.cs", false)]
	[InlineData(@"..\Elsewhere\A.cs;..\Shared\B.cs", true)]
	public void CompileIncludeDetection(string include, bool expected) {
		string projectDirectory = Path.Combine(_root, "Client");
		string shared = Path.Combine(_root, "Shared") + Path.DirectorySeparatorChar;

		Assert.Equal(expected, SharedSourceProjects.CompilesFrom($"<Project><ItemGroup><Compile Include=\"{include}\" /></ItemGroup></Project>", projectDirectory, shared));
	}

	[Fact]
	public void UnionDeduplicatesAcrossProjects() {
		ServerDiagnostic shared = new("a.cs", 1, 1, "same", DiagnosticSeverity.Error, "CS1", 1, 4);
		ServerDiagnostic client = new("a.cs", 2, 1, "client only", DiagnosticSeverity.Error, "CS2", 2, 4);
		ServerDiagnostic server = new("a.cs", 3, 1, "server only", DiagnosticSeverity.Warning, "CS3", 3, 4);

		List<ServerDiagnostic> merged = DiagnosticRequests.Union([[shared, client], [shared, server]]);

		Assert.Equal(3, merged.Count);
		Assert.Contains(client, merged);
		Assert.Contains(server, merged);
	}

	[Fact]
	public void UnionKeepsDistinctSeverities() {
		ServerDiagnostic warning = new("a.cs", 1, 1, "m", DiagnosticSeverity.Warning, "X1");
		ServerDiagnostic error = warning with { Severity = DiagnosticSeverity.Error };

		Assert.Equal(2, DiagnosticRequests.Union([[warning], [error]]).Count);
	}

	[Fact]
	public void UnionOfNothingIsEmpty() => Assert.Empty(DiagnosticRequests.Union([]));
}
