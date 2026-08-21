using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json;

using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Dotnet;

public static class LanguageServerAcquisition
{
	private const string Feed =
		"https://pkgs.dev.azure.com/azure-public/vside/_packaging/vs-impl/nuget/v3/flat2";

	private static string PackageId =>
		$"microsoft.codeanalysis.languageserver.win-{Architecture}";

	private static string Architecture =>
		System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch {
			System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
			_ => "x64",
		};

	public static string InstallDirectory(string storageDirectory) =>
		Path.Combine(storageDirectory, "roslyn");

	public static async Task<ServerLaunch?> AcquireAsync(string storageDirectory, string? dotnetPath,
		Action<string> progress, CancellationToken cancellation = default) {
		string target = InstallDirectory(storageDirectory);

		try {
			using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
			client.DefaultRequestHeaders.Add("User-Agent", "Forge");

			progress("Resolving the latest Microsoft.CodeAnalysis.LanguageServer...");

			if (await LatestVersionAsync(client, cancellation).ConfigureAwait(false) is not { } version) {
				progress("Could not read the package feed. Set dotnet.server.path to a local copy.");
				return null;
			}

			progress($"Downloading {PackageId} {version}...");

			string url = $"{Feed}/{PackageId}/{version}/{PackageId}.{version}.nupkg";

			using HttpResponseMessage response = await client
				.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)
				.ConfigureAwait(false);

			if (!response.IsSuccessStatusCode) {
				progress($"Download failed: the feed returned {(int)response.StatusCode}.");
				return null;
			}

			string staging = target + ".downloading";
			Delete(staging);
			Directory.CreateDirectory(staging);

			string archive = Path.Combine(staging, "package.nupkg");

			await using (FileStream file = File.Create(archive)) {
				await response.Content.CopyToAsync(file, cancellation).ConfigureAwait(false);
			}

			progress("Extracting...");
			Extract(archive, staging);
			File.Delete(archive);

			if (!Replace(staging, target, progress)) return null;

			if (LanguageServerLocator.Locate(null, storageDirectory, dotnetPath) is not { } launch) {
				progress("The package did not contain a language server.");
				return null;
			}

			progress($"Installed Microsoft.CodeAnalysis.LanguageServer {version}.");
			return launch;
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException
			or TaskCanceledException or JsonException or InvalidDataException
			or UnauthorizedAccessException) {
			progress($"Could not download the language server: {exception.Message}");
			return null;
		}
	}

	private static async Task<string?> LatestVersionAsync(HttpClient client, CancellationToken cancellation) {
		string index = await client.GetStringAsync($"{Feed}/{PackageId}/index.json", cancellation)
			.ConfigureAwait(false);

		if (JsonNode.Parse(index) is not JsonObject root ||
			root["versions"] is not JsonArray versions || versions.Count == 0) return null;

		string? best = null;

		foreach (JsonNode? entry in versions) {
			if (entry?.GetValue<string>() is { Length: > 0 } version &&
				(best is null || Compare(version, best) > 0)) best = version;
		}

		return best;
	}

	private static int Compare(string left, string right) {
		(int[] leftRelease, string leftPre) = Split(left);
		(int[] rightRelease, string rightPre) = Split(right);

		for (int i = 0; i < Math.Max(leftRelease.Length, rightRelease.Length); i++) {
			int a = i < leftRelease.Length ? leftRelease[i] : 0;
			int b = i < rightRelease.Length ? rightRelease[i] : 0;

			if (a != b) return a.CompareTo(b);
		}

		if (leftPre.Length == 0) return rightPre.Length == 0 ? 0 : 1;

		if (rightPre.Length == 0) return -1;

		string[] leftParts = leftPre.Split('.');
		string[] rightParts = rightPre.Split('.');

		for (int i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++) {
			if (i >= leftParts.Length) return -1;

			if (i >= rightParts.Length) return 1;

			bool leftNumeric = int.TryParse(leftParts[i], out int leftNumber);
			bool rightNumeric = int.TryParse(rightParts[i], out int rightNumber);

			if (leftNumeric && rightNumeric) {
				if (leftNumber != rightNumber) return leftNumber.CompareTo(rightNumber);

				continue;
			}

			if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;

			int text = string.CompareOrdinal(leftParts[i], rightParts[i]);

			if (text != 0) return text;
		}

		return 0;

		static (int[] Release, string Prerelease) Split(string version) {
			int dash = version.IndexOf('-');
			string release = dash < 0 ? version : version[..dash];
			string prerelease = dash < 0 ? string.Empty : version[(dash + 1)..];

			string[] parts = release.Split('.');
			int[] numbers = new int[parts.Length];

			for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], out numbers[i]);

			return (numbers, prerelease);
		}
	}

	private static void Extract(string archive, string target) {
		string prefix = $"content/LanguageServer/win-{Architecture}/";

		using ZipArchive zip = ZipFile.OpenRead(archive);

		foreach (ZipArchiveEntry entry in zip.Entries) {
			if (entry.Length == 0 && entry.Name.Length == 0) continue;

			string name = entry.FullName.Replace('\\', '/');

			if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

			string relative = name[prefix.Length..];
			string destination = Path.GetFullPath(Path.Combine(target, relative));

			if (!destination.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;

			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			entry.ExtractToFile(destination, overwrite: true);
		}
	}

	private static bool Replace(string staging, string target, Action<string> progress) {
		try {
			if (Directory.Exists(target)) {
				string retired = $"{target}.old-{DateTime.Now:yyyyMMddHHmmss}";

				Directory.Move(target, retired);
				Delete(retired);
			}

			Directory.Move(staging, target);
			SweepRetired(target);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			progress($"Could not replace the installed server: {exception.Message}. " +
				"Close Forge and delete the roslyn folder if this keeps happening.");

			return false;
		}
	}

	private static void SweepRetired(string target) {
		string? parent = Path.GetDirectoryName(target);

		if (parent is null) return;

		foreach (string retired in Directory.EnumerateDirectories(parent,
			$"{Path.GetFileName(target)}.old-*")) Delete(retired);
	}

	private static void Delete(string directory) {
		try {
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}
}
