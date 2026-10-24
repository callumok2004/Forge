namespace Forge.Common.Commands;

public interface ICommand
{
	string GetId();
	string GetTitle();
	string? GetCategory();
	string GetLabel();
	string? GetKeybinding();
	bool IsEnabled();

	void Run(ICommandHost host);
}
