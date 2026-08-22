using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet;

public static class NetCoreDbgAcquisition
{
	private const string LatestRelease =
		"https://api.github.com/repos/Samsung/netcoredbg/releases/latest";

	public static string InstallDirectory(string storageDirectory) =>
		Path.Combine(storageDirectory, "debuggers", "netcoredbg");

	public static string ExecutablePath(string storageDirectory) =>
		Path.Combine(InstallDirectory(storageDirectory), "netcoredbg.exe");

	private static bool Arm64 =>
		System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
			== System.Runtime.InteropServices.Architecture.Arm64;

	public static bool IsInstalled(string storageDirectory) => File.Exists(ExecutablePath(storageDirectory));

	public static async Task<bool> AcquireAsync(string storageDirectory, Action<string> progress,
		CancellationToken cancellation = default) {
		if (IsInstalled(storageDirectory)) return true;

		string target = InstallDirectory(storageDirectory);

		try {
			using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
			client.DefaultRequestHeaders.Add("User-Agent", "Forge");

			progress("Resolving the latest netcoredbg release...");

			if (await AssetUrlAsync(client, cancellation).ConfigureAwait(false) is not { } url) {
				progress("Could not find a Windows netcoredbg asset in the latest release.");
				return false;
			}

			progress($"Downloading {url}...");

			using HttpResponseMessage response = await client
				.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)
				.ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				progress($"Download failed: GitHub returned {(int)response.StatusCode}.");
				return false;
			}

			string staging = target + ".downloading";
			Delete(staging);
			Directory.CreateDirectory(staging);

			string archive = Path.Combine(staging, "netcoredbg.zip");

			await using (FileStream file = File.Create(archive)) {
				await response.Content.CopyToAsync(file, cancellation).ConfigureAwait(false);
			}

			progress("Extracting...");
			ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
			File.Delete(archive);

			if (FindExecutable(staging) is not { } found) {
				progress("The archive did not contain netcoredbg.exe.");
				Delete(staging);
				return false;
			}

			string source = Path.GetDirectoryName(found)!;

			Delete(target);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			Directory.Move(source, target);
			Delete(staging);

			progress($"netcoredbg is installed at {ExecutablePath(storageDirectory)}.");
			return true;
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException
			or UnauthorizedAccessException or InvalidDataException or TaskCanceledException) {
			progress($"Could not install netcoredbg: {exception.Message}");
			return false;
		}
	}

	private static async Task<string?> AssetUrlAsync(HttpClient client, CancellationToken cancellation) {
		string body = await client.GetStringAsync(LatestRelease, cancellation).ConfigureAwait(false);

		if (JsonNode.Parse(body)?["assets"] is not JsonArray assets) return null;

		string? fallback = null;

		foreach (JsonNode? asset in assets) {
			if (asset?["name"]?.GetValue<string>() is not { } name) continue;
			if (!name.Contains("win", StringComparison.OrdinalIgnoreCase)) continue;
			if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

			string? url = asset["browser_download_url"]?.GetValue<string>();

			if (name.Contains("arm64", StringComparison.OrdinalIgnoreCase) != Arm64) {
				fallback ??= url;
				continue;
			}

			return url;
		}

		return fallback;
	}

	private static string? FindExecutable(string directory) {
		foreach (string path in Directory.EnumerateFiles(directory, "netcoredbg.exe",
			SearchOption.AllDirectories)) {
			return path;
		}

		return null;
	}

	private static void Delete(string path) {
		try {
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}
}
