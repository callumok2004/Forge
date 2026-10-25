using Forge.Common.Workspace;
using Forge.Core;
using Forge.Workspace;

namespace Forge.Tests;

public class StateMemoryTests
{
	[Fact]
	public void TouchMovesToFrontAndCaps() {
		List<string> list = ["a", "b", "c"];

		GlobalMemory.Touch(list, "C", 3);
		GlobalMemory.Touch(list, "d", 3);

		Assert.Equal(["d", "C", "a"], list);
	}

	[Fact]
	public void GlobalMemoryRoundTrips() {
		string path = Path.Combine(Path.GetTempPath(), "forge-state-" + Guid.NewGuid().ToString("N") + ".json");

		try {
			GlobalMemory memory = GlobalMemory.Load(path);
			memory.RecentCommands.Add("workbench.action.files.save");
			memory.TouchFolder(@"C:\repo");
			memory.Save();

			GlobalMemory loaded = GlobalMemory.Load(path);

			Assert.Equal(["workbench.action.files.save"], loaded.RecentCommands);
			Assert.Equal([@"C:\repo"], loaded.RecentFolders);
		}
		finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void ExplorerExpansionRoundTrips() {
		string root = Path.Combine(Path.GetTempPath(), "forge-tree-" + Guid.NewGuid().ToString("N"));
		string nested = Path.Combine(root, "src", "inner");
		Directory.CreateDirectory(nested);
		Directory.CreateDirectory(Path.Combine(root, "docs"));

		try {
			FileTree tree = new(root);
			FileNode src = tree.GetRoot().Children!.Single(node => node.Name == "src");
			tree.Toggle(src);
			tree.Toggle(src.Children!.Single(node => node.Name == "inner"));

			List<string> expanded = [];
			tree.CaptureExpanded(expanded);

			Assert.Equal([Path.Combine(root, "src"), nested], expanded);

			FileTree restored = new(root);
			restored.RestoreExpanded(expanded);
			List<FileNode> rows = [];
			restored.Flatten(rows);

			Assert.Equal(["docs", "src", "inner"], rows.Select(node => node.Name));
		}
		finally {
			Directory.Delete(root, recursive: true);
		}
	}
}
