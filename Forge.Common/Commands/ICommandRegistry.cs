namespace Forge.Common.Commands;

public interface ICommandRegistry
{
	IReadOnlyList<ICommand> All();

	bool Register(ICommand command);
	ICommand? Find(string id);
	ICommand? Resolve(uint key, bool control, bool shift, bool alt);
	bool Execute(string id, ICommandHost host);
}
