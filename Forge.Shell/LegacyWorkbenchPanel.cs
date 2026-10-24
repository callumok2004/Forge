using Forge.Common.Render;
using Forge.Common.UI;

namespace Forge.Shell;

public sealed class LegacyWorkbenchPanel : Panel
{
	private readonly Workbench _workbench;

	public LegacyWorkbenchPanel(Workbench workbench) {
		_workbench = workbench;
		Dock = DockType.Fill;
		MouseInputEnabled = false;
	}

	public override void Paint(IPainter painter) => _workbench.Render(painter, Wide, Tall);
}
