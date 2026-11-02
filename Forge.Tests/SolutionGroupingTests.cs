using Forge.Common.Workspace;
using Forge.Extensions.Dotnet.Solution;
using Forge.Search;
using Forge.Workspace;

namespace Forge.Tests;

public class SolutionGroupingTests
{
	private const string Base = @"C:\work";

	[Fact]
	public void SlnxNestsFoldersByPath() {
		List<SolutionNode> roots = SolutionParser.ParseSlnx("""
			<Solution>
			  <Folder Name="/1. Foundation/">
			    <Project Path="Forge.Core/Forge.Core.csproj" />
			  </Folder>
			  <Folder Name="/1. Foundation/Text/">
			    <Project Path="Forge.Text/Forge.Text.csproj" />
			  </Folder>
			  <Project Path="Forge.App/Forge.App.csproj" />
			</Solution>
			""", Base);

		Assert.Equal(2, roots.Count);
		SolutionNode foundation = roots[0];
		Assert.Equal("1. Foundation", foundation.Name);
		Assert.Equal(["Forge.Core", "Text"], foundation.Children.Select(child => child.Name));
		Assert.Equal(@"C:\work\Forge.Text\Forge.Text.csproj", foundation.Children[1].Children[0].Path);
		Assert.Equal(SolutionNodeKind.Project, roots[1].Kind);
	}

	[Fact]
	public void SlnReadsNestingAndSolutionItems() {
		List<SolutionNode> roots = SolutionParser.ParseSln("""
			Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Docs", "Docs", "{11111111-1111-1111-1111-111111111111}"
				ProjectSection(SolutionItems) = preProject
					README.md = README.md
				EndProjectSection
			EndProject
			Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{22222222-2222-2222-2222-222222222222}"
			EndProject
			Global
				GlobalSection(NestedProjects) = preSolution
					{22222222-2222-2222-2222-222222222222} = {11111111-1111-1111-1111-111111111111}
				EndGlobalSection
			EndGlobal
			""", Base);

		SolutionNode docs = Assert.Single(roots);
		Assert.Equal(SolutionNodeKind.Folder, docs.Kind);
		Assert.Equal([@"C:\work\README.md", @"C:\work\src\App\App.csproj"], docs.Children.Select(child => child.Path));
	}

	[Theory]
	[InlineData("net build", ".NET: Build", true)]
	[InlineData("dotnet build", ".NET: Build", false)]
	[InlineData("tog term", "View: Toggle Terminal", true)]
	[InlineData("zoom", "View: Zoom In", true)]
	[InlineData("dbn", ".NET: Build", false)]
	[InlineData("vtt", "View: Toggle Terminal", true)]
	public void WordFilterMatchesLikeVsCode(string query, string label, bool expected) => Assert.Equal(expected, WordFilter.Matches(query, label));

	[Fact]
	public void GroupsClaimProjectFoldersAndKeepTheRest() {
		string root = Directory.CreateTempSubdirectory("forge-groups").FullName;

		try {
			Directory.CreateDirectory(Path.Combine(root, "Core"));
			Directory.CreateDirectory(Path.Combine(root, "tools"));
			File.WriteAllText(Path.Combine(root, "README.md"), string.Empty);

			FileTree tree = new(root);
			tree.SetGroups([new ExplorerGroup("1. Foundation", [], [Path.Combine(root, "Core")])]);

			List<FileNode> rows = [];
			tree.Flatten(rows);

			Assert.Equal(["1. Foundation", "tools", "README.md"], rows.Select(row => row.Name));
			Assert.True(rows[0].IsGroup);

			tree.Toggle(rows[0]);
			tree.Flatten(rows);

			Assert.Equal(["1. Foundation", "Core", "tools", "README.md"], rows.Select(row => row.Name));
			Assert.Equal(1, rows[1].Depth);

			tree.SetGroups(null);
			tree.Flatten(rows);

			Assert.Equal(["Core", "tools", "README.md"], rows.Select(row => row.Name));
			Assert.Equal(0, rows[0].Depth);
		}
		finally {
			Directory.Delete(root, recursive: true);
		}
	}
}
