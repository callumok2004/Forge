global using static Forge.Commands.ForgeDllMain;

using Forge.Common;
using Forge.Common.Commands;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Commands;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static ICommandRegistry Registry { get; private set; } = null!;

	[Dependency] public static IOutputChannels Output { get; private set; } = null!;

	public static void Link(IServiceCollection services) {
		services.AddSingleton<ICommandRegistry, CommandRegistry>();
		services.AddSingleton<IOutputChannels, OutputChannels>();
	}
}
