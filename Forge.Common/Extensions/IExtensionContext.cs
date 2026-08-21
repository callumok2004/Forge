using Forge.Common.Commands;
using Forge.Core;

namespace Forge.Common.Extensions;

public interface IExtensionContext
{
	ICommandHost GetHost();
	Settings GetSettings();

	string GetStorageDirectory();

	void OnDeactivate(Action handler);
}
