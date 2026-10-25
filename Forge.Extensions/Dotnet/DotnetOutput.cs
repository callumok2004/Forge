using Forge.Common.Commands;

namespace Forge.Extensions.Dotnet;

public sealed class DotnetOutput
{
	private IOutputChannel? _channel;

	public IOutputChannel Channel(ICommandHost host) => _channel ??= host.Channel(".NET");

	public void Log(ICommandHost host, string line) => Channel(host).Append(line);

	public void Show(ICommandHost host) => host.ShowOutput(Channel(host));
}
