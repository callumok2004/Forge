global using static Forge.Webview.ForgeDllMain;

using Forge.Common;
using Forge.Common.Webview;

using Microsoft.Extensions.DependencyInjection;

namespace Forge.Webview;

[ForgeComponent]
public static class ForgeDllMain
{
	[Dependency] public static IWebviewHost Webviews { get; private set; } = NullWebviewHost.Instance;

	public static void Link(IServiceCollection services) {
	}
}
