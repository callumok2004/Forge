using System.Diagnostics;

namespace Forge.Debug;

public sealed record DebugAdapterDescriptor(string Type, string Label, string FileName,
	IReadOnlyList<string> Arguments)
{
	public Func<Action<string>, Task<bool>>? Acquire { get; init; }

	public bool Exists => File.Exists(FileName);
}

public static class DebugAdapters
{
	private static readonly Dictionary<string, DebugAdapterDescriptor> Registered =
		new(StringComparer.OrdinalIgnoreCase);

	public static void Register(DebugAdapterDescriptor descriptor) =>
		Registered[descriptor.Type] = descriptor;

	public static IReadOnlyCollection<DebugAdapterDescriptor> All => Registered.Values;

	public static DebugAdapterDescriptor? For(string type) =>
		Registered.TryGetValue(type, out DebugAdapterDescriptor? descriptor) ? descriptor : null;

	public static ProcessStartInfo? StartInfoFor(string type, string workingDirectory) {
		if (For(type) is not { } descriptor) return null;
		if (!File.Exists(descriptor.FileName)) return null;

		ProcessStartInfo info = new() {
			FileName = descriptor.FileName,
			WorkingDirectory = workingDirectory,
		};

		foreach (string argument in descriptor.Arguments) info.ArgumentList.Add(argument);

		return info;
	}
}
