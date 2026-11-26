using Forge.Shell;

namespace Forge.Tests;

public sealed class DiffTabModelTests : IDisposable
{
	private readonly string _directory = Directory.CreateTempSubdirectory("forge-diff-model").FullName;

	public DiffTabModelTests() => typeof(Forge.Workbench.ForgeDllMain).GetProperty(nameof(Forge.Workbench.ForgeDllMain.Channels))!.SetValue(null, new Forge.Commands.OutputChannels());

	public void Dispose() => Directory.Delete(_directory, recursive: true);

	[Fact]
	public void OpeningAFileWithAnOpenDiffTabSharesTheDiffBuffer() {
		string path = Path.Combine(_directory, "a.cs");
		File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n");
		using Forge.Shell.Workbench workbench = new(1f);

		Assert.NotNull(workbench.OpenDiffTab(path, "git:a.cs~", "a.cs (Working Tree)"));
		workbench.OpenFile(path);

		Forge.Workspace.Document diff = workbench.Documents.Single(static document => document.Source is not null);
		Forge.Workspace.Document file = workbench.Documents.Single(static document => document.Path is not null);

		Assert.Same(diff.Source, file);
		Assert.Same(diff.Buffer, file.Buffer);
	}

	[Fact]
	public void EditsInTheFileTabReachTheDiffTabAndSaveOnce() {
		string path = Path.Combine(_directory, "b.cs");
		File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n");
		using Forge.Shell.Workbench workbench = new(1f);

		workbench.OpenDiffTab(path, "git:b.cs~", "b.cs (Working Tree)");
		workbench.OpenFile(path);

		Forge.Workspace.Document diff = workbench.Documents.Single(static document => document.Source is not null);
		Forge.Workspace.Document file = workbench.Documents.Single(static document => document.Path is not null);

		file.Buffer.Insert(0, "using System;\r\n");
		Assert.True(diff.IsDirty());

		Assert.True(file.Save());
		Assert.False(diff.IsDirty());
		Assert.Equal("using System;\r\none\r\ntwo\r\nthree\r\n", File.ReadAllText(path));
	}
}
