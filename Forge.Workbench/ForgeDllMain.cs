global using static Forge.Workbench.ForgeDllMain;

using Forge.Common;
using Forge.Common.Commands;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Workbench;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IOutputChannels Channels { get; private set; } = null!;

	public static void Link(IServiceCollection services) { }
}
