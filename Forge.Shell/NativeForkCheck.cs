#if FORGE_DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;

using Forge.Common.Commands;

namespace Forge.Shell;

public static class NativeForkCheck
{
	public static void Start(Workbench workbench) => _ = Task.Run(() => CheckAsync(workbench));

	private static string RepositoryRoot([CallerFilePath] string source = "") => Path.GetDirectoryName(Path.GetDirectoryName(source)!)!;

	private static async Task CheckAsync(Workbench workbench) {
		string root = RepositoryRoot();
		string modules = Path.Combine(root, ".gitmodules");

		if (!File.Exists(modules)) return;

		foreach ((string path, string upstream) in Upstreams(modules)) {
			string directory = Path.Combine(root, path);

			if (!Directory.Exists(directory)) continue;

			string? head = (await GitAsync(directory, $"ls-remote {upstream} HEAD").ConfigureAwait(false))?.Split('\t')[0];

			if (head is not { Length: 40 }) continue;

			if (await GitAsync(directory, $"merge-base --is-ancestor {head} HEAD").ConfigureAwait(false) is not null) continue;

			workbench.Post(() => workbench.Notifications.Show(MessageSeverity.Warning, $"{path} is behind its upstream ({upstream}); rebase the fork to pick up the latest changes.", [], null));
		}
	}

	private static IEnumerable<(string Path, string Upstream)> Upstreams(string modules) {
		string? path = null;

		foreach (string raw in File.ReadLines(modules)) {
			ReadOnlySpan<char> line = raw.AsSpan().Trim();

			if (line.StartsWith("[submodule")) path = null;
			else if (Value(line, "path") is { } value) path = value;
			else if (Value(line, "upstream") is { } upstream && path is not null) yield return (path, upstream);
		}
	}

	private static string? Value(ReadOnlySpan<char> line, ReadOnlySpan<char> key) {
		if (!line.StartsWith(key)) return null;

		ReadOnlySpan<char> rest = line[key.Length..].TrimStart();

		return rest.StartsWith("=") ? rest[1..].Trim().ToString() : null;
	}

	private static async Task<string?> GitAsync(string directory, string arguments) {
		try {
			using Process? git = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });

			if (git is null) return null;

			Task<string> output = git.StandardOutput.ReadToEndAsync();
			_ = git.StandardError.ReadToEndAsync();
			await git.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

			return git.ExitCode == 0 ? (await output.ConfigureAwait(false)).Trim() : null;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException) {
			return null;
		}
	}
}
#endif
