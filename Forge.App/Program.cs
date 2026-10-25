using Microsoft.Extensions.DependencyInjection;

using Forge.Common;
using Forge.Shell;

namespace Forge.App;

public static class Program
{
	[STAThread]
	public static int Main(string[] args) =>
		new ForgeBuilder()
			.WithAssembly("Forge.Render")
			.WithAssembly("Forge.UI")
			.WithAssembly("Forge.Workbench")
			.WithAssembly("Forge.Editor")
			.WithAssembly("Forge.Commands")
			.WithAssembly("Forge.Workspace")
			.WithAssembly("Forge.Git")
			.WithAssembly("Forge.Language")
			.WithAssembly("Forge.Debug")
			.WithAssembly("Forge.Extensions")
			.WithAssembly("Forge.VSCode")
			.WithAssemblyOf<ShellApplication>()
			.Build()
			.GetRequiredService<IForgeApplication>()
			.Run(args);
}
