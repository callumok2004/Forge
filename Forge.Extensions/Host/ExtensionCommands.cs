using Forge.Commands;
using Forge.Common.Commands;
using Forge.Common.Extensions;

namespace Forge.Extensions.Host;

public static class ExtensionCommands
{
	public static void Register(ICommandRegistry commands, IExtensionHost extensions) {
		commands.Register(new Command("extensions.list", "Show Extensions", "Extensions", host => {
			IOutputChannel channel = host.Channel("Extensions");

			foreach (IForgeExtension extension in extensions.All()) {
				string state = extensions.IsActive(extension.GetIdentifier()) ? "active" : "idle";
				channel.Append($"{extension.GetIdentifier()}  [{state}]  {extension.GetDisplayName()}");
			}

			host.ShowOutput(channel);
		}));

		foreach (IForgeExtension extension in extensions.All()) {
			string identifier = extension.GetIdentifier();
			string name = extension.GetDisplayName();

			commands.Register(new Command($"{identifier}.activate", $"Activate {name}", "Extensions", host => {
				host.Log(extensions.Load(identifier, host) ? $"{name} activated." : $"{name} is already active.");
			}));

			commands.Register(new Command($"{identifier}.deactivate", $"Deactivate {name}", "Extensions", host => {
				host.Log(extensions.Unload(identifier) ? $"{name} deactivated." : $"{name} was not active.");
			}));

			commands.Register(new Command($"{identifier}.reload", $"Reload {name}", "Extensions", host => {
				extensions.Unload(identifier);
				host.Log(extensions.Load(identifier, host) ? $"{name} reloaded." : $"{name} could not be reloaded.");
			}));
		}
	}
}
