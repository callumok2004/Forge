using Forge.Common.Commands;

namespace Forge.Commands;

public sealed class CommandRegistry : ICommandRegistry
{
	private readonly Dictionary<string, ICommand> _byId = [with(StringComparer.Ordinal)];
	private readonly List<ICommand> _ordered = [];

	public IReadOnlyList<ICommand> All() => _ordered;

	public bool Register(ICommand command) {
		if (!_byId.TryAdd(command.GetId(), command)) return false;

		_ordered.Add(command);
		return true;
	}

	public ICommand? Find(string id) => _byId.GetValueOrDefault(id);

	public bool Execute(string id, ICommandHost host) {
		if (_byId.GetValueOrDefault(id) is not { } command) return false;

		command.Run(host);
		return true;
	}
}
