namespace Forge.Common.Commands;

public interface ICommandRegistry
{
	IReadOnlyList<ICommand> All();

	bool Register(ICommand command);
	ICommand? Find(string id);
	bool Execute(string id, ICommandHost host);
}
