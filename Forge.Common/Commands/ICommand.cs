namespace Forge.Common.Commands;

public interface ICommand
{
	string GetId();
	string GetTitle();
	string? GetCategory();
	string GetLabel();

	void Run(ICommandHost host);
}
