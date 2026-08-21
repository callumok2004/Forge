global using static Forge.UI.ForgeDllMain;

using Forge.Common;
using Forge.Common.UI;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.UI;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IAppWindow Window { get; private set; } = null!;

	public static void Link(IServiceCollection services) { }
}
