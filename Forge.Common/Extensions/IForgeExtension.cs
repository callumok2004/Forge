using Forge.Common.Commands;
using Forge.Common.UI;

namespace Forge.Common.Extensions;

public interface IForgeExtension
{
	string GetIdentifier();
	string GetDisplayName();

	ActivationEvent GetActivation();

	bool ActivatesIn(string? workspacePath);
	bool ActivatesFor(string filePath);
	bool ActivatesForCommand(string commandId);

	void Register(ICommandRegistry commands);

	void RegisterViews(IWorkbenchViews views) { }

	void Activate(IExtensionContext context);
	void Deactivate();
}
