global using static Forge.VSCode.ForgeDllMain;

using Forge.Common;
using Forge.Common.Render;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.VSCode;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IAmbient Ambient { get; private set; } = NullAmbient.Instance;

	public static void Link(IServiceCollection services) { }
}
