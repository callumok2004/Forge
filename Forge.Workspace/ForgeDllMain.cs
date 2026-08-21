global using static Forge.Workspace.ForgeDllMain;

using Forge.Common;
using Forge.Common.Workspace;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Workspace;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IFileTree Tree { get; private set; } = null!;

	[Dependency] public static IFileIndex Index { get; private set; } = null!;

	public static void Link(IServiceCollection services) { }
}
