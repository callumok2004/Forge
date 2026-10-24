global using static Forge.Shell.ForgeDllMain;

using Forge.Common;
using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Common.Render;
using Forge.Common.UI;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Shell;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IAppWindow Window { get; private set; } = null!;

	[Dependency] public static IRenderDevice Device { get; private set; } = null!;

	[Dependency] public static IFontSet Fonts { get; private set; } = null!;

	[Dependency] public static ICommandRegistry Registry { get; private set; } = null!;

	[Dependency] public static IOutputChannels Channels { get; private set; } = null!;

	[Dependency] public static IEditorHost Editor { get; private set; } = null!;

	[Dependency] public static IAmbient Ambient { get; private set; } = NullAmbient.Instance;

	public static void Link(IServiceCollection services) { }
}
