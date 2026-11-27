using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.VSCode.Gallery;

public sealed record GalleryFile(string AssetType, string Source);

public sealed record GalleryExtension(string Publisher, string Name, string DisplayName, string Description, string Version)
{
	public string Id => $"{Publisher}.{Name}";

	public string PublisherDisplayName { get; init; } = string.Empty;

	public bool Verified { get; init; }

	public long Installs { get; init; }

	public double Rating { get; init; }

	public int RatingCount { get; init; }

	public DateTimeOffset? LastUpdated { get; init; }

	public string? TargetPlatform { get; init; }

	public string? Engine { get; init; }

	public bool Preview { get; init; }

	public IReadOnlyList<string> Categories { get; init; } = [];

	public IReadOnlyList<GalleryFile> Files { get; init; } = [];

	public string? Asset(string type) => Files.FirstOrDefault(file => file.AssetType == type)?.Source;
}

public static class GalleryClient
{
	public const string ServiceUrl = "https://marketplace.visualstudio.com/_apis/public/gallery";
	public const string ItemUrl = "https://marketplace.visualstudio.com/items";
	public const string ControlUrl = "https://main.vscode-cdn.net/extensions/marketplace.json";

	public const string IconAsset = "Microsoft.VisualStudio.Services.Icons.Default";
	public const string ReadmeAsset = "Microsoft.VisualStudio.Services.Content.Details";
	public const string ChangelogAsset = "Microsoft.VisualStudio.Services.Content.Changelog";
	public const string ManifestAsset = "Microsoft.VisualStudio.Code.Manifest";
	public const string PackageAsset = "Microsoft.VisualStudio.Services.VSIXPackage";

	private const string CodeVersion = "1.130.0";
	private const string Platform = "win32-x64";

	private const int FilterTarget = 8;
	private const int FilterSearchText = 10;
	private const int FilterExtensionName = 7;
	private const int FilterExcludeWithFlags = 12;

	private const int IncludeFiles = 0x2;
	private const int IncludeCategoryAndTags = 0x4;
	private const int IncludeVersionProperties = 0x10;
	private const int ExcludeNonValidated = 0x20;
	private const int IncludeAssetUri = 0x80;
	private const int IncludeStatistics = 0x100;
	private const int IncludeLatestVersionOnly = 0x200;
	private const int Unpublished = 0x1000;

	private static readonly HttpClient Http = Create();

	private static HttpClient Create() {
		HttpClient client = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(60) };
		client.DefaultRequestHeaders.TryAddWithoutValidation("X-Market-Client-Id", $"VSCode {CodeVersion}");
		client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"VSCode {CodeVersion} (Forge)");
		return client;
	}

	public static Task<List<GalleryExtension>> SearchAsync(string text, int pageSize, CancellationToken cancellation) =>
		QueryAsync([(FilterSearchText, text)], pageSize, text.Length == 0 ? 4 : 0, cancellation);

	public static async Task<GalleryExtension?> LatestAsync(string id, CancellationToken cancellation) =>
		(await QueryAsync([(FilterExtensionName, id)], 1, 0, cancellation).ConfigureAwait(false)).FirstOrDefault();

	public static async Task<Dictionary<string, GalleryExtension>> LatestAsync(IReadOnlyCollection<string> ids, CancellationToken cancellation) {
		Dictionary<string, GalleryExtension> found = new(StringComparer.OrdinalIgnoreCase);

		if (ids.Count == 0) return found;

		foreach (GalleryExtension extension in await QueryAsync([.. ids.Select(static id => (FilterExtensionName, id))], ids.Count, 0, cancellation).ConfigureAwait(false)) found[extension.Id] = extension;

		return found;
	}

	private static async Task<List<GalleryExtension>> QueryAsync(IReadOnlyList<(int Type, string Value)> criteria, int pageSize, int sortBy, CancellationToken cancellation) {
		JsonArray filters = [new JsonObject { ["filterType"] = FilterTarget, ["value"] = "Microsoft.VisualStudio.Code" }, new JsonObject { ["filterType"] = FilterExcludeWithFlags, ["value"] = Unpublished.ToString(System.Globalization.CultureInfo.InvariantCulture) }];

		foreach ((int type, string value) in criteria) {
			if (value.Length > 0) filters.Add(new JsonObject { ["filterType"] = type, ["value"] = value });
		}

		JsonObject body = new() {
			["filters"] = new JsonArray(new JsonObject { ["criteria"] = filters, ["pageNumber"] = 1, ["pageSize"] = pageSize, ["sortBy"] = sortBy, ["sortOrder"] = 0 }),
			["assetTypes"] = new JsonArray(),
			["flags"] = IncludeFiles | IncludeCategoryAndTags | IncludeVersionProperties | ExcludeNonValidated | IncludeAssetUri | IncludeStatistics | IncludeLatestVersionOnly,
		};

		using HttpRequestMessage request = new(HttpMethod.Post, ServiceUrl + "/extensionquery") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
		request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;api-version=3.0-preview.1"));

		using HttpResponseMessage response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);

		if (!response.IsSuccessStatusCode) return [];

		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
		using JsonDocument document = await JsonDocument.ParseAsync(stream, default, cancellation).ConfigureAwait(false);

		List<GalleryExtension> results = [];

		if (!document.RootElement.TryGetProperty("results", out JsonElement resultSets) || resultSets.GetArrayLength() == 0) return results;

		if (!resultSets[0].TryGetProperty("extensions", out JsonElement extensions)) return results;

		foreach (JsonElement extension in extensions.EnumerateArray()) {
			if (Parse(extension) is { } parsed) results.Add(parsed);
		}

		return results;
	}

	private static GalleryExtension? Parse(in JsonElement extension) {
		if (Text(extension, "extensionName") is not { } name || !extension.TryGetProperty("publisher", out JsonElement publisher) || Text(publisher, "publisherName") is not { } publisherName) return null;

		if (!extension.TryGetProperty("versions", out JsonElement versions) || Pick(versions) is not { } version) return null;

		List<GalleryFile> files = [];

		if (version.TryGetProperty("files", out JsonElement fileArray)) {
			foreach (JsonElement file in fileArray.EnumerateArray()) {
				if (Text(file, "assetType") is { } type && Text(file, "source") is { } source) files.Add(new GalleryFile(type, source));
			}
		}

		string? engine = null;

		if (version.TryGetProperty("properties", out JsonElement properties)) {
			foreach (JsonElement property in properties.EnumerateArray()) {
				if (Text(property, "key") == "Microsoft.VisualStudio.Code.Engine") engine = Text(property, "value");
			}
		}

		long installs = 0;
		double rating = 0d;
		int ratingCount = 0;

		if (extension.TryGetProperty("statistics", out JsonElement statistics)) {
			foreach (JsonElement statistic in statistics.EnumerateArray()) {
				double value = statistic.TryGetProperty("value", out JsonElement number) && number.ValueKind == JsonValueKind.Number ? number.GetDouble() : 0d;

				switch (Text(statistic, "statisticName")) {
					case "install": installs = (long)value; break;
					case "averagerating": rating = value; break;
					case "ratingcount": ratingCount = (int)value; break;
				}
			}
		}

		List<string> categories = [];

		if (extension.TryGetProperty("categories", out JsonElement categoryArray)) {
			foreach (JsonElement category in categoryArray.EnumerateArray()) {
				if (category.GetString() is { } text) categories.Add(text);
			}
		}

		string flags = Text(publisher, "flags") ?? string.Empty;
		bool verified = flags.Contains("verified", StringComparison.OrdinalIgnoreCase) || (publisher.TryGetProperty("isDomainVerified", out JsonElement domain) && domain.ValueKind == JsonValueKind.True);

		return new GalleryExtension(publisherName, name, Text(extension, "displayName") ?? name, Text(extension, "shortDescription") ?? string.Empty, Text(version, "version") ?? "0.0.0") {
			PublisherDisplayName = Text(publisher, "displayName") ?? publisherName,
			Verified = verified,
			Installs = installs,
			Rating = rating,
			RatingCount = ratingCount,
			LastUpdated = DateTimeOffset.TryParse(Text(version, "lastUpdated"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset updated) ? updated : null,
			TargetPlatform = Text(version, "targetPlatform"),
			Engine = engine,
			Preview = (Text(extension, "flags") ?? string.Empty).Contains("preview", StringComparison.OrdinalIgnoreCase),
			Categories = categories,
			Files = files,
		};
	}

	private static JsonElement? Pick(in JsonElement versions) {
		JsonElement? universal = null;

		foreach (JsonElement version in versions.EnumerateArray()) {
			string? platform = Text(version, "targetPlatform");

			if (platform == Platform) return version;

			if (platform is null or "universal" && universal is null) universal = version;
		}

		return universal;
	}

	public static async Task<string?> TextAssetAsync(GalleryExtension extension, string type, CancellationToken cancellation) {
		if (extension.Asset(type) is not { } source) return null;

		try {
			return await Http.GetStringAsync(source, cancellation).ConfigureAwait(false);
		}
		catch (HttpRequestException) {
			return null;
		}
	}

	public static async Task<byte[]?> BytesAsync(string url, CancellationToken cancellation) {
		try {
			return await Http.GetByteArrayAsync(url, cancellation).ConfigureAwait(false);
		}
		catch (HttpRequestException) {
			return null;
		}
	}

	public static async Task<HashSet<string>> MaliciousAsync(CancellationToken cancellation) {
		HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);

		try {
			using JsonDocument document = JsonDocument.Parse(await Http.GetStringAsync(ControlUrl, cancellation).ConfigureAwait(false));

			if (document.RootElement.TryGetProperty("malicious", out JsonElement malicious)) {
				foreach (JsonElement item in malicious.EnumerateArray()) {
					if (item.GetString() is { } id) blocked.Add(id);
				}
			}
		}
		catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException) {
		}

		return blocked;
	}

	public static bool IsBlocked(HashSet<string> malicious, GalleryExtension extension) => malicious.Contains(extension.Id) || malicious.Contains(extension.Publisher);

	public static async Task<string> InstallAsync(GalleryExtension extension, string root, CancellationToken cancellation) {
		string source = extension.Asset(PackageAsset) ?? throw new InvalidOperationException("The gallery did not return a package for this extension.");

		if (extension.TargetPlatform is { } platform && !source.Contains("targetPlatform=", StringComparison.Ordinal)) source += (source.Contains('?') ? "&" : "?") + "targetPlatform=" + platform;

		byte[] package = await Http.GetByteArrayAsync(source, cancellation).ConfigureAwait(false);

		Directory.CreateDirectory(root);
		string target = Path.Combine(root, $"{extension.Id}-{extension.Version}".ToLowerInvariant());
		string staging = target + ".installing";

		if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

		Directory.CreateDirectory(staging);
		string stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

		using (ZipArchive archive = new(new MemoryStream(package), ZipArchiveMode.Read)) {
			foreach (ZipArchiveEntry entry in archive.Entries) {
				if (!entry.FullName.StartsWith("extension/", StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;

				string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName["extension/".Length..]));

				if (!destination.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Package entry escapes the extension folder: {entry.FullName}");

				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				entry.ExtractToFile(destination, overwrite: true);
			}
		}

		if (!File.Exists(Path.Combine(staging, "package.json"))) {
			Directory.Delete(staging, recursive: true);
			throw new InvalidDataException("The package has no extension manifest.");
		}

		foreach (string existing in Directory.EnumerateDirectories(root)) {
			if (existing.EndsWith(".installing", StringComparison.OrdinalIgnoreCase)) continue;

			if (VSCodeExtensions.Read(existing) is { } installed && string.Equals(installed.Identifier, extension.Id, StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFullPath(existing), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) {
				try {
					Directory.Delete(existing, recursive: true);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				}
			}
		}

		if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

		Directory.Move(staging, target);
		return target;
	}

	public const string RemovalMarker = ".forge-uninstalled";

	public static void Uninstall(string directory) {
		if (!Directory.Exists(directory)) return;

		try {
			Directory.Delete(directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			File.WriteAllText(Path.Combine(directory, RemovalMarker), string.Empty);
			throw;
		}
	}

	private static string? Text(in JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
