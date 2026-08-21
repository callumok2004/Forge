global using static Forge.Editor.ForgeDllMain;

using Forge.Common;
using Forge.Common.Editor;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Editor;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IEditorHost Host { get; private set; } = null!;

	public static void Link(IServiceCollection services) { }
}
