using Forge.Git;

namespace Forge.Tests;

public sealed class GitRepositoryDiscoveryTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-discovery-" + Guid.NewGuid().ToString("N"));

	public GitRepositoryDiscoveryTests() => Directory.CreateDirectory(_root);

	public void Dispose() {
		try {
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException) {
		}
	}

	private string Folder(string relative) {
		string path = Path.GetFullPath(Path.Combine(_root, relative));
		Directory.CreateDirectory(path);
		return path;
	}

	private string Repo(string relative, bool file = false) {
		string path = Folder(relative);

		if (file) File.WriteAllText(Path.Combine(path, ".git"), "gitdir: ../.git/modules/x");
		else Directory.CreateDirectory(Path.Combine(path, ".git"));

		return path;
	}

	private void Submodules(string repo, params string[] paths) => File.WriteAllLines(Path.Combine(repo, ".gitmodules"), paths.SelectMany(static path => (string[])[$"[submodule \"{path}\"]", $"\tpath = {path}", "\turl = https://example.invalid/x.git"]));

	private static string? FindRoot(string path) {
		for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) {
			string dotGit = Path.Combine(current, ".git");

			if (Directory.Exists(dotGit) || File.Exists(dotGit)) return current;
		}

		return null;
	}

	private GitRepositoryDiscovery Discover(string workspace, GitDiscoveryOptions? options = null, IReadOnlySet<string>? closed = null) {
		GitRepositoryDiscovery discovery = new(workspace, options ?? new GitDiscoveryOptions(), FindRoot, closed);
		discovery.Run();
		return discovery;
	}

	private List<string> Relative(GitRepositoryDiscovery discovery) => [.. discovery.Roots.Select(root => Path.GetRelativePath(_root, root))];

	[Fact]
	public void NoRepositories() => Assert.Empty(Discover(Folder("plain")).Roots);

	[Fact]
	public void WorkspaceRootRepository() {
		string workspace = Repo("ws");

		Assert.Equal(["ws"], Relative(Discover(workspace)));
	}

	[Fact]
	public void ChildRepositoriesAtDepthOne() {
		string workspace = Folder("ws");
		Repo("ws/a");
		Repo("ws/b");

		Assert.Equal([Path.Combine("ws", "a"), Path.Combine("ws", "b")], Relative(Discover(workspace)).Order().ToList());
	}

	[Fact]
	public void DeeperRepositoriesNeedLargerDepth() {
		string workspace = Folder("ws");
		Repo("ws/a/b");

		Assert.Empty(Discover(workspace).Roots);
		Assert.Equal([Path.Combine("ws", "a", "b")], Relative(Discover(workspace, new GitDiscoveryOptions { RepositoryScanMaxDepth = 2 })));
	}

	[Fact]
	public void UnlimitedDepth() {
		string workspace = Folder("ws");
		Repo("ws/a/b/c/d");

		Assert.Equal([Path.Combine("ws", "a", "b", "c", "d")], Relative(Discover(workspace, new GitDiscoveryOptions { RepositoryScanMaxDepth = -1 })));
	}

	[Fact]
	public void IgnoredScanFoldersAreSkipped() {
		string workspace = Folder("ws");
		Repo("ws/node_modules");
		Repo("ws/Node_Modules2");

		Assert.Equal([Path.Combine("ws", "Node_Modules2")], Relative(Discover(workspace)));
	}

	[Fact]
	public void DotGitFolderIsNotTraversed() {
		string workspace = Repo("ws");
		Repo("ws/.git/inner");

		Assert.Equal(["ws"], Relative(Discover(workspace, new GitDiscoveryOptions { RepositoryScanMaxDepth = 3 })));
	}

	[Fact]
	public void SubfolderScanningCanBeDisabled() {
		string workspace = Repo("ws");
		Repo("ws/a");

		Assert.Equal(["ws"], Relative(Discover(workspace, new GitDiscoveryOptions { AutoRepositoryDetection = "openEditors" })));
		Assert.Equal(["ws"], Relative(Discover(workspace, new GitDiscoveryOptions { AutoRepositoryDetection = "false" })));
	}

	[Fact]
	public void NestedRepositoryInsideWorkspaceRepository() {
		string workspace = Repo("ws");
		Repo("ws/nested");

		Assert.Equal(["ws", Path.Combine("ws", "nested")], Relative(Discover(workspace)));
	}

	[Fact]
	public void PlainSubfolderOfRepositoryIsNotARepository() {
		string workspace = Repo("ws");
		Folder("ws/src");

		Assert.Equal(["ws"], Relative(Discover(workspace)));
	}

	[Fact]
	public void SubmodulesAreOpenedInDeclarationOrder() {
		string workspace = Repo("ws");
		Repo("ws/native/second", file: true);
		Repo("ws/native/first", file: true);
		Submodules(workspace, "native/second", "native/first");

		Assert.Equal(["ws", Path.Combine("ws", "native", "second"), Path.Combine("ws", "native", "first")], Relative(Discover(workspace)));
	}

	[Fact]
	public void SubmodulesNotCheckedOutAreSkipped() {
		string workspace = Repo("ws");
		Folder("ws/native/empty");
		Submodules(workspace, "native/empty");

		Assert.Equal(["ws"], Relative(Discover(workspace)));
	}

	[Fact]
	public void SubmoduleDetectionCanBeDisabled() {
		string workspace = Repo("ws");
		Repo("ws/native/sub", file: true);
		Submodules(workspace, "native/sub");

		Assert.Equal(["ws"], Relative(Discover(workspace, new GitDiscoveryOptions { DetectSubmodules = false })));
	}

	[Fact]
	public void SubmoduleLimitOpensOnlyTheFirstOnesAndReports() {
		string workspace = Repo("ws");
		string[] paths = [.. Enumerable.Range(0, 4).Select(static i => $"m/s{i}")];

		foreach (string path in paths) Repo("ws/" + path, file: true);

		Submodules(workspace, paths);

		GitRepositoryDiscovery discovery = Discover(workspace, new GitDiscoveryOptions { DetectSubmodulesLimit = 2 });

		Assert.Equal(3, discovery.Roots.Count);
		Assert.Equal(4, Assert.Single(discovery.TooManySubmodules).Count);
	}

	[Fact]
	public void NestedSubmodulesAreOpened() {
		string workspace = Repo("ws");
		string outer = Repo("ws/outer", file: true);
		Repo("ws/outer/inner", file: true);
		Submodules(workspace, "outer");
		Submodules(outer, "inner");

		Assert.Equal(["ws", Path.Combine("ws", "outer"), Path.Combine("ws", "outer", "inner")], Relative(Discover(workspace)));
	}

	[Fact]
	public void SubmoduleAlsoFoundByScanIsOpenedOnce() {
		string workspace = Repo("ws");
		Repo("ws/sub", file: true);
		Submodules(workspace, "sub");

		Assert.Equal(["ws", Path.Combine("ws", "sub")], Relative(Discover(workspace)));
	}

	[Fact]
	public void ParentRepositoryIsReportedNotOpened() {
		Repo("parent");
		string workspace = Folder("parent/child");

		GitRepositoryDiscovery discovery = Discover(workspace);

		Assert.Empty(discovery.Roots);
		Assert.Equal(Path.Combine(_root, "parent"), Assert.Single(discovery.ParentRepositories));
	}

	[Fact]
	public void ParentRepositoryOpensWhenAlwaysAllowed() {
		Repo("parent");
		string workspace = Folder("parent/child");

		Assert.Equal(["parent"], Relative(Discover(workspace, new GitDiscoveryOptions { OpenRepositoryInParentFolders = "always" })));
	}

	[Fact]
	public void ParentRepositoryNeverIsStillNotOpened() {
		Repo("parent");
		string workspace = Folder("parent/child");

		Assert.Empty(Discover(workspace, new GitDiscoveryOptions { OpenRepositoryInParentFolders = "never" }).Roots);
	}

	[Fact]
	public void TrustedParentRepositoryOpens() {
		string parent = Repo("parent");
		string workspace = Folder("parent/child");
		GitRepositoryDiscovery discovery = new(workspace, new GitDiscoveryOptions(), FindRoot, trustedParents: new HashSet<string>([parent], StringComparer.OrdinalIgnoreCase));

		discovery.Run();

		Assert.Equal(["parent"], Relative(discovery));
	}

	[Fact]
	public void ExplicitParentOpenWorks() {
		Repo("parent");
		string workspace = Folder("parent/child");
		GitRepositoryDiscovery discovery = Discover(workspace);

		Assert.True(discovery.Open(discovery.ParentRepositories[0], openIfParent: true));
		Assert.Equal(["parent"], Relative(discovery));
	}

	[Fact]
	public void IgnoredRepositoriesRelativeAndAbsolute() {
		string workspace = Folder("ws");
		Repo("ws/a");
		string b = Repo("ws/b");
		Repo("ws/c");

		GitRepositoryDiscovery discovery = Discover(workspace, new GitDiscoveryOptions { IgnoredRepositories = ["a", b] });

		Assert.Equal([Path.Combine("ws", "c")], Relative(discovery));
	}

	[Fact]
	public void ClosedRepositoriesStayClosedUnlessForced() {
		string workspace = Folder("ws");
		string a = Repo("ws/a");
		GitRepositoryDiscovery discovery = Discover(workspace, closed: new HashSet<string>([a], StringComparer.OrdinalIgnoreCase));

		Assert.Empty(discovery.Roots);
		Assert.True(discovery.Open(a, openIfClosed: true));
	}

	[Fact]
	public void ScanRepositoriesAddsDeeperPaths() {
		string workspace = Folder("ws");
		Repo("ws/deep/er/repo");

		Assert.Equal([Path.Combine("ws", "deep", "er", "repo")], Relative(Discover(workspace, new GitDiscoveryOptions { ScanRepositories = ["deep/er/repo"] })));
	}

	[Fact]
	public void ScanRepositoriesRejectsAbsoluteAndDotGit() {
		string workspace = Folder("ws");
		string deep = Repo("ws/x/y");

		Assert.Empty(Discover(workspace, new GitDiscoveryOptions { ScanRepositories = [deep, ".git"] }).Roots);
	}

	[Fact]
	public void OpenEditorFileOutsideKnownRepositoriesOpensItsRepository() {
		string workspace = Folder("ws");
		string deep = Repo("ws/a/b/c");
		string file = Path.Combine(Folder("ws/a/b/c/src"), "x.cs");
		File.WriteAllText(file, "");
		GitRepositoryDiscovery discovery = Discover(workspace);

		Assert.Equal(deep, discovery.OpenFor(file));
	}

	[Fact]
	public void OpenEditorFileInsideKnownRepositoryOpensNothing() {
		string workspace = Repo("ws");
		Repo("ws/a/b/c");
		string file = Path.Combine(Folder("ws/a/b/c/src"), "x.cs");
		File.WriteAllText(file, "");
		GitRepositoryDiscovery discovery = Discover(workspace);

		Assert.Null(discovery.OpenFor(file));
		Assert.Single(discovery.Roots);
	}

	[Fact]
	public void OpenEditorsDisabledBySubFoldersOnly() {
		string workspace = Folder("ws");
		Repo("ws/a/b/c");
		string file = Path.Combine(Folder("ws/a/b/c/src"), "x.cs");
		File.WriteAllText(file, "");
		GitRepositoryDiscovery discovery = Discover(workspace, new GitDiscoveryOptions { AutoRepositoryDetection = "subFolders" });

		Assert.Null(discovery.OpenFor(file));
	}

	[Fact]
	public void ContainingPicksDeepestRepository() {
		string workspace = Repo("ws");
		string nested = Repo("ws/nested");
		GitRepositoryDiscovery discovery = Discover(workspace);

		Assert.Equal(nested, discovery.Containing(Path.Combine(nested, "file.cs")));
		Assert.Equal(workspace, discovery.Containing(Path.Combine(workspace, "other", "file.cs")));
		Assert.Null(discovery.Containing(Path.Combine(_root, "elsewhere.cs")));
	}

	[Fact]
	public void SimilarPrefixIsNotContainment() {
		string workspace = Folder("ws");
		string a = Repo("ws/app");
		GitRepositoryDiscovery discovery = Discover(workspace);

		Assert.Null(discovery.Containing(Path.Combine(workspace, "apple", "x.cs")));
		Assert.Equal(a, discovery.Containing(Path.Combine(a, "x.cs")));
	}

	[Fact]
	public void SubmoduleFileParsing() {
		string repo = Folder("parse");
		File.WriteAllText(Path.Combine(repo, ".gitmodules"), "[submodule \"a\"]\n  path=lib/a\n  url = x\n[submodule \"b\"]\n\tpath = lib/b \n\tpathological = nope\n");

		Assert.Equal([Path.Combine("lib", "a"), Path.Combine("lib", "b")], GitRepositoryDiscovery.Submodules(repo));
	}
}
