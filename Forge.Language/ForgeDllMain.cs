global using static Forge.Language.ForgeDllMain;

using Forge.Common;
using Forge.Common.Language;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Language;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IIntelliSense Completion { get; private set; } = null!;

	public static void Link(IServiceCollection services) { }
}
