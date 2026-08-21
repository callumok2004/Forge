using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Dotnet;

public sealed record ServerLaunch(string Command, string[] Arguments, string ServerDirectory);

public static class LanguageServerLocator
{
	private const string ServerName = "Microsoft.CodeAnalysis.LanguageServer";

	public static string DotnetPath(string? configured = null) {
		if (configured is { Length: > 0 }) return configured;

		string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT")
			?? Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");

		if (root is { Length: > 0 }) {
			string muxer = Path.Combine(root, "dotnet.exe");

			if (File.Exists(muxer)) return muxer;
		}

		return "dotnet.exe";
	}

	public static ServerLaunch? Locate(string? configuredPath, string storageDirectory, string? dotnetPath = null) {
		if (configuredPath is { Length: > 0 }) {
			string directory = configuredPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
				configuredPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
					? Path.GetDirectoryName(configuredPath) ?? configuredPath
					: configuredPath;

			return ServerIn(directory, dotnetPath);
		}

		return ServerIn(Path.Combine(storageDirectory, "roslyn"), dotnetPath);
	}

	private static ServerLaunch? ServerIn(string directory, string? dotnetPath) {
		string executable = Path.Combine(directory, $"{ServerName}.exe");

		if (File.Exists(executable)) return new ServerLaunch(executable, [], directory);

		string library = Path.Combine(directory, $"{ServerName}.dll");

		return File.Exists(library)
			? new ServerLaunch(DotnetPath(dotnetPath), [library], directory)
			: null;
	}

	public static string? DesignTimeTargets(string serverDirectory) {
		string targets = Path.Combine(serverDirectory, "Targets",
			"Microsoft.CSharpExtension.DesignTime.targets");

		return File.Exists(targets) ? targets : null;
	}
}
