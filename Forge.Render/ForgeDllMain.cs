global using static Forge.Render.ForgeDllMain;

using Forge.Common;
using Forge.Common.Render;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Render;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IRenderDevice Device { get; private set; } = null!;

	[Dependency] public static IFontSet Fonts { get; private set; } = null!;

	[Dependency] public static IFileIconSet Icons { get; private set; } = null!;

	[Dependency] public static IFrameStats Stats { get; private set; } = null!;

	[Dependency] public static IAmbient Background { get; private set; } = NullAmbient.Instance;

	public static void Link(IServiceCollection services) => services.AddSingleton<IFrameStats, FrameStats>();
}
