using Forge.Common.Commands;

namespace Forge.Commands;

public sealed record Command(string Id, string Title, string? Category, Action<ICommandHost> Handler) : ICommand
{
	public string GetId() => Id;

	public string GetTitle() => Title;

	public string? GetCategory() => Category;

	public string GetLabel() => Category is { Length: > 0 } category ? $"{category}: {Title}" : Title;

	public void Run(ICommandHost host) => Handler(host);
}
