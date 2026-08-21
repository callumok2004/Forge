using Forge.Common.Extensions;
using Forge.Extensions.Discord;
using Forge.Extensions.Dotnet;
using Forge.Extensions.MergeConflict;
using Forge.Extensions.TrailingSpaces;

namespace Forge.Extensions.Host;

public static class BuiltInExtensions
{
	public static IReadOnlyList<IForgeExtension> All { get; } =
	[
		new DotnetExtension(),
		new TrailingSpacesExtension(),
		new MergeConflictExtension(),
		new DiscordPresenceExtension(),
	];

	public static T Get<T>() where T : IForgeExtension => All.OfType<T>().First();
}
