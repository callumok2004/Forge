using Forge.Common.Commands;

namespace Forge.Common.Extensions;

public interface IExtensionHost
{
	IReadOnlyList<IForgeExtension> All();
	IReadOnlyList<string> Active();

	bool IsActive(string identifier);

	int RegisterAll(ICommandRegistry commands);

	void Startup(ICommandHost host);
	void FileOpened(ICommandHost host, string path);
	void CommandInvoked(ICommandHost host, string commandId);

	bool Load(string identifier, ICommandHost host);
	bool Unload(string identifier);

	void UnloadAll();
}
