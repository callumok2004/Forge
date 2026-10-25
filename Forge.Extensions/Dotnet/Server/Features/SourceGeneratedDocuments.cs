using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class SourceGeneratedDocuments(RoslynLanguageClient client)
{
	public const string Scheme = "roslyn-source-generated://";

	private readonly Dictionary<string, string> _paths = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, string> _uris = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	public string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "Forge", "SourceGenerated");

	public static bool IsGenerated(string uri) => uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

	public string? UriFor(string path) {
		lock (_sync) return _uris.GetValueOrDefault(path);
	}

	private static ulong StableHash(string text) {
		ulong hash = 14695981039346656037;

		foreach (char character in text) hash = (hash ^ character) * 1099511628211;

		return hash;
	}

	public async Task<string?> MaterialiseAsync(string uri) {
		JsonNode? result = await client.RequestAsync("sourceGeneratedDocument/_roslyn_getText", new JsonObject {
			["textDocument"] = new JsonObject { ["uri"] = uri },
		}).ConfigureAwait(false);

		if (result?["text"]?.GetValue<string>() is not { } text) return null;

		string path;

		lock (_sync) {
			if (!_paths.TryGetValue(uri, out path!)) {
				string name = Uri.UnescapeDataString(uri[(uri.LastIndexOf('/') + 1)..]);
				int query = name.IndexOf('?');

				if (query >= 0) name = name[..query];

				path = Path.Combine(Directory, StableHash(uri).ToString("x16"), name.Length > 0 ? name : "Generated.cs");
				_paths[uri] = path;
				_uris[path] = uri;
			}
		}

		try {
			System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

			if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);

			await File.WriteAllTextAsync(path, text).ConfigureAwait(false);
			File.SetAttributes(path, FileAttributes.ReadOnly);
			return path;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return null;
		}
	}
}
