namespace Forge.Shell;

public sealed record InputTarget(string Name, Func<bool> IsActive, Action<char>? Type = null, Action? DeleteLeft = null, Func<uint, bool, bool>? Key = null);
