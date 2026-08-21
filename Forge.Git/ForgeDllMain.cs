global using static Forge.Git.ForgeDllMain;

using Forge.Common;
using Forge.Common.Git;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Git;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IGitRepository Repository { get; private set; } = null!;

	public static void Link(IServiceCollection services) { }
}
