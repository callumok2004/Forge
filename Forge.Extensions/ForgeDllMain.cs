global using static Forge.Extensions.ForgeDllMain;

using Forge.Common;
using Forge.Common.Extensions;
using Forge.Extensions.Host;

using Forge.Common.Render;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Extensions;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IExtensionRegistry Registry { get; private set; } = null!;

	[Dependency] public static IExtensionHost Extensions { get; private set; } = null!;

	[Dependency] public static IAmbient Ambient { get; private set; } = NullAmbient.Instance;

	public static void Link(IServiceCollection services) {
		services.AddSingleton<IExtensionRegistry, ExtensionRegistry>();
		services.AddSingleton<IExtensionHost>(_ => new ExtensionHost(Core.Settings.Current, BuiltInExtensions.All));
	}
}
