using Forge.Common.Git;
using Forge.Git;
using Forge.Workspace;

namespace Forge.Tests;

public sealed class GitRepositoriesTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-repositories-" + Guid.NewGuid().ToString("N"));

	public GitRepositoriesTests() => Directory.CreateDirectory(_root);

	public void Dispose() {
		try {
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException) {
		}
	}

	private string Repo(string relative, bool file = false) {
		string path = Path.GetFullPath(Path.Combine(_root, relative));
		Directory.CreateDirectory(path);

		if (file) File.WriteAllText(Path.Combine(path, ".git"), "gitdir: x");
		else Directory.CreateDirectory(Path.Combine(path, ".git"));

		return path;
	}

	private static string? FindRoot(string path) {
		for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) {
			string dotGit = Path.Combine(current, ".git");

			if (Directory.Exists(dotGit) || File.Exists(dotGit)) return current;
		}

		return null;
	}

	private GitRepositories Open(string workspace, HashSet<string>? closed = null) {
		GitRepositories repositories = new(workspace, new GitDiscoveryOptions(), closed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), FindRoot);
		repositories.Discover();
		return repositories;
	}

	private string Path2(params string[] parts) => Path.Combine([_root, .. parts]);

	[Fact]
	public void WorkingTreeFileMapsToDeepestRepository() {
		string workspace = Repo("ws");
		string nested = Repo("ws/lib");
		GitRepositories repositories = Open(workspace);

		Assert.Equal(nested, repositories.Owner(Path.Combine(nested, "a.cs"))?.GetRootPath());
		Assert.Equal(workspace, repositories.Owner(Path.Combine(workspace, "src", "b.cs"))?.GetRootPath());
	}

	[Fact]
	public void DotGitStateFilesMapToTheirRepository() {
		string workspace = Repo("ws");
		string nested = Repo("ws/lib");
		GitRepositories repositories = Open(workspace);

		Assert.Equal(workspace, repositories.Owner(Path.Combine(workspace, ".git", "index"))?.GetRootPath());
		Assert.Equal(nested, repositories.Owner(Path.Combine(nested, ".git", "refs", "heads", "main"))?.GetRootPath());
	}

	[Fact]
	public void SubmoduleGitDirInsideParentMapsToSubmodule() {
		string workspace = Repo("ws");
		string submodule = Repo("ws/native/sub", file: true);
		File.WriteAllText(Path.Combine(workspace, ".gitmodules"), "[submodule \"native/sub\"]\n\tpath = native/sub\n");
		GitRepositories repositories = Open(workspace);

		Assert.Equal(submodule, repositories.Owner(Path.Combine(workspace, ".git", "modules", "native", "sub", "HEAD"))?.GetRootPath());
		Assert.Equal(submodule, repositories.Owner(Path.Combine(workspace, ".git", "modules", "native", "sub", "refs", "heads", "x"))?.GetRootPath());
	}

	[Fact]
	public void GithubFolderIsNotGitInternals() {
		string workspace = Repo("ws");
		GitRepositories repositories = Open(workspace);

		Assert.Equal(workspace, repositories.Owner(Path.Combine(workspace, ".github", "workflows", "ci.yml"))?.GetRootPath());
	}

	[Fact]
	public void PathOutsideAllRepositoriesHasNoOwner() {
		string workspace = Repo("ws");
		GitRepositories repositories = Open(workspace);

		Assert.Null(repositories.Owner(Path2("elsewhere", "x.cs")));
	}

	[Fact]
	public void CloseRemovesAndRemembers() {
		string workspace = Repo("ws");
		Repo("ws/lib");
		GitRepositories repositories = Open(workspace);
		IGitRepository lib = repositories.GetAll()[1];
		int changes = 0;
		repositories.OnChanged(() => changes++);

		repositories.Close(lib);

		Assert.Single(repositories.GetAll());
		Assert.Contains(lib.GetRootPath(), repositories.Closed);
		Assert.Equal(1, changes);
	}

	[Fact]
	public void ClosedRepositoryStaysClosedOnNextDiscovery() {
		string workspace = Repo("ws");
		string lib = Repo("ws/lib");

		GitRepositories repositories = Open(workspace, new HashSet<string>([lib], StringComparer.OrdinalIgnoreCase));

		Assert.Single(repositories.GetAll());
	}

	[Fact]
	public void ReopeningAClosedRepository() {
		string workspace = Repo("ws");
		string lib = Repo("ws/lib");
		GitRepositories repositories = Open(workspace, new HashSet<string>([lib], StringComparer.OrdinalIgnoreCase));

		Assert.NotNull(repositories.Open(lib, openIfParent: false));
		Assert.Equal(2, repositories.GetAll().Count);
		Assert.DoesNotContain(lib, repositories.Closed);
	}

	[Fact]
	public void NewRepositoryCreatedAtRuntimeIsOpened() {
		string workspace = Repo("ws");
		GitRepositories repositories = Open(workspace);
		string created = Repo("ws/fresh");

		repositories.OpenPossible([Path.Combine(created, ".git", "HEAD")]);

		Assert.Equal(2, repositories.GetAll().Count);
		Assert.Equal(created, repositories.For(Path.Combine(created, "x.cs"))?.GetRootPath());
	}

	[Fact]
	public void DiscoveryOrderIsRootThenChildren() {
		string workspace = Repo("ws");
		Repo("ws/a");

		Assert.Equal([workspace, Path.Combine(workspace, "a")], Open(workspace).GetAll().Select(static repository => repository.GetRootPath()));
	}

	[Theory]
	[InlineData("HEAD", true)]
	[InlineData("index", true)]
	[InlineData("index.lock", false)]
	[InlineData("packed-refs", true)]
	[InlineData("FETCH_HEAD", true)]
	[InlineData("objects\\ab\\cdef", false)]
	[InlineData("logs\\HEAD", false)]
	[InlineData("refs\\heads\\main", true)]
	[InlineData("modules\\native\\sub\\HEAD", true)]
	[InlineData("modules\\native\\sub\\index", true)]
	[InlineData("modules\\native\\sub\\objects\\aa\\bb", false)]
	[InlineData("modules\\a\\modules\\b\\HEAD", true)]
	public void GitStateChanges(string path, bool expected) => Assert.Equal(expected, WorkspaceWatcher.IsGitStateChange(path));
}
