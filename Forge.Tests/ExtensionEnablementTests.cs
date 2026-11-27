using Forge.Core;

namespace Forge.Tests;

public class ExtensionEnablementTests
{
	private static (ExtensionEnablement Enablement, GlobalMemory Global, WorkspaceMemory Workspace) Create() {
		GlobalMemory global = new();
		WorkspaceMemory workspace = new();
		return (new ExtensionEnablement(global, workspace), global, workspace);
	}

	[Fact]
	public void EnabledByDefault() => Assert.True(Create().Enablement.IsEnabled("forge.glua"));

	[Fact]
	public void WorkspaceOverrideWinsOverGlobal() {
		(ExtensionEnablement enablement, _, _) = Create();

		enablement.Set("forge.glua", false, EnablementScope.Global);
		enablement.Set("forge.glua", true, EnablementScope.Workspace);

		Assert.False(enablement.IsGloballyEnabled("forge.glua"));
		Assert.True(enablement.IsEnabled("forge.glua"));
		Assert.True(enablement.WorkspaceOverride("FORGE.GLUA"));
	}

	[Fact]
	public void MatchingGlobalClearsOverride() {
		(ExtensionEnablement enablement, _, WorkspaceMemory workspace) = Create();

		enablement.Set("forge.discord", false, EnablementScope.Workspace);
		enablement.Set("forge.discord", true, EnablementScope.Workspace);

		Assert.Empty(workspace.ExtensionOverrides);
		Assert.True(enablement.IsEnabled("forge.discord"));
	}

	[Fact]
	public void GlobalChangeDropsWorkspaceOverride() {
		(ExtensionEnablement enablement, GlobalMemory global, WorkspaceMemory workspace) = Create();

		enablement.Set("forge.discord", false, EnablementScope.Workspace);
		enablement.Set("forge.discord", false, EnablementScope.Global);

		Assert.Equal(["forge.discord"], global.DisabledExtensions);
		Assert.Empty(workspace.ExtensionOverrides);
	}
}
