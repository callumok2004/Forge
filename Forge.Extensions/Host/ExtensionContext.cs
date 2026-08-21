using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Core;

namespace Forge.Extensions.Host;

public sealed class ExtensionContext(string identifier, ICommandHost host, Settings settings) : IExtensionContext
{
	private Action? _deactivate;

	public ICommandHost GetHost() => host;

	public Settings GetSettings() => settings;

	public string GetStorageDirectory() {
		string path = Path.Combine(UserData.ExtensionStorageDirectory, identifier);
		Directory.CreateDirectory(path);
		return path;
	}

	public void OnDeactivate(Action handler) => _deactivate += handler;

	public void Dispose() {
		_deactivate?.Invoke();
		_deactivate = null;
	}
}
