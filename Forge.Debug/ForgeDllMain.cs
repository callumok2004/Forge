global using static Forge.Debug.ForgeDllMain;

using Forge.Common;
using Forge.Common.Debug;
using Forge.Common.Render;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Debug;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IDebug Debugger { get; private set; } = null!;

	[Dependency] public static IAmbient Ambient { get; private set; } = NullAmbient.Instance;

	public static void Link(IServiceCollection services) { }
}
