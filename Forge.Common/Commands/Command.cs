namespace Forge.Common.Commands;

public sealed record Command(string Id, string Title, string? Category, Action<ICommandHost> Handler, string? Keybinding = null, Func<bool>? When = null) : ICommand
{
	public string GetId() => Id;

	public string GetTitle() => Title;

	public string? GetCategory() => Category;

	private string? _label;

	public string GetLabel() => _label ??= Category is { Length: > 0 } category ? $"{category}: {Title}" : Title;

	public string? GetKeybinding() => Keybinding;

	public bool IsEnabled() => When?.Invoke() ?? true;

	public void Run(ICommandHost host) => Handler(host);
}
