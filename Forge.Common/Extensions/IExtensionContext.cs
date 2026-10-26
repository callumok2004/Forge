using Forge.Common.Commands;
using Forge.Common.Debug;
using Forge.Common.UI;
using Forge.Core;

namespace Forge.Common.Extensions;

public interface IExtensionContext
{
	ICommandHost GetHost();

	IWorkbenchViews? GetViews() => null;

	IDebug? GetDebug() => null;
	Settings GetSettings();

	string GetStorageDirectory();

	void OnDeactivate(Action handler);
}
