using Forge.Common.Commands;
using Forge.UI.Framework;

namespace Forge.Shell.Actions;

public static class DeveloperCommands
{
	public static void Register(ICommandRegistry registry, UiSystem ui) {
		PanelTree tree = new(ui);

		registry.Register(new Command("forgeui_drawtree", "Toggle Panel Tree", "Developer", _ => tree.Toggle()));
		registry.Register(new Command("forgeui_visualizelayout", "Toggle Layout Visualisation", "Developer", _ => ui.VisualizeLayout = !ui.VisualizeLayout));
		registry.Register(new Command("forgeui_visualizepaint", "Toggle Paint Visualisation", "Developer", _ => ui.VisualizePaint = !ui.VisualizePaint));
		registry.Register(new Command("forge_throwexception", "Throw Test Exception", "Developer", _ => throw new InvalidOperationException("forge_throwexception: test exception thrown on purpose.")));
	}
}
