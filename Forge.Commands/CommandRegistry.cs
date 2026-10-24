using Forge.Common.Commands;

namespace Forge.Commands;

public sealed class CommandRegistry : ICommandRegistry
{
	private readonly Dictionary<string, ICommand> _byId = [with(StringComparer.Ordinal)];
	private readonly Dictionary<Keybinding, List<ICommand>> _byKey = [];
	private readonly List<ICommand> _ordered = [];

	public IReadOnlyList<ICommand> All() => _ordered;

	public bool Register(ICommand command) {
		if (!_byId.TryAdd(command.GetId(), command)) return false;

		_ordered.Add(command);

		foreach (string text in (command.GetKeybinding() ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries)) {
			if (!Keybinding.TryParse(text, out Keybinding keybinding)) continue;

			if (!_byKey.TryGetValue(keybinding, out List<ICommand>? bound)) _byKey[keybinding] = bound = [];

			bound.Add(command);
		}

		return true;
	}

	public ICommand? Find(string id) => _byId.GetValueOrDefault(id);

	public ICommand? Resolve(uint key, bool control, bool shift, bool alt) {
		if (!_byKey.TryGetValue(Keybinding.Normalise(key, control, shift, alt), out List<ICommand>? bound)) return null;

		foreach (ICommand command in bound)
			if (command.IsEnabled()) return command;

		return null;
	}

	public bool Execute(string id, ICommandHost host) {
		if (_byId.GetValueOrDefault(id) is not { } command || !command.IsEnabled()) return false;

		command.Run(host);
		return true;
	}
}
