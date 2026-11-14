using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class SemanticTokenRequests(RoslynLanguageClient client)
{
	private string[] _tokenTypes = [];
	private int _staticModifier = -1;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<ServerToken>> _cache = new(StringComparer.OrdinalIgnoreCase);

	internal void ReadLegend(JsonNode? legend) {
		if (legend?["tokenTypes"] is JsonArray types) _tokenTypes = [.. types.Select(entry => entry?.GetValue<string>() ?? string.Empty)];

		if (legend?["tokenModifiers"] is JsonArray modifiers) {
			for (int i = 0; i < modifiers.Count; i++)
				if (modifiers[i]?.GetValue<string>() == "static") _staticModifier = i;
		}
	}

	internal void Forget(string path) => _cache.TryRemove(path, out _);

	public IReadOnlyList<ServerToken>? Cached(string path) => _cache.TryGetValue(path, out IReadOnlyList<ServerToken>? tokens) ? tokens : null;

	public async Task<IReadOnlyList<ServerToken>> FullAsync(string path) {
		if (_tokenTypes.Length == 0 || !client.Documents.IsOpen(path)) return [];

		System.Text.Json.JsonElement? result = await client.RequestElementAsync("textDocument/semanticTokens/full", new JsonObject {
			["textDocument"] = client.Contexts.Identifier(path),
		}).ConfigureAwait(false);

		List<ServerToken> tokens = Decode(result.Property("data"), _tokenTypes, _staticModifier);

		_cache[path] = tokens;
		return tokens;
	}

	internal static List<ServerToken> Decode(System.Text.Json.JsonElement? dataElement, string[] tokenTypes, int staticModifier) {
		if (dataElement is not { ValueKind: System.Text.Json.JsonValueKind.Array } data || data.GetArrayLength() < 5) return [];

		List<ServerToken> tokens = [with(data.GetArrayLength() / 5)];
		Span<int> values = stackalloc int[5];
		int filled = 0;
		int line = 0;
		int character = 0;

		foreach (System.Text.Json.JsonElement number in data.EnumerateArray()) {
			values[filled++] = number.TryGetInt32(out int value) ? value : 0;

			if (filled < 5) continue;

			filled = 0;
			int lineDelta = values[0];
			int startDelta = values[1];
			int length = values[2];
			int type = values[3];
			int modifiers = values[4];

			line += lineDelta;
			character = lineDelta == 0 ? character + startDelta : startDelta;

			if (length > 0 && type >= 0 && type < tokenTypes.Length) tokens.Add(new ServerToken(line + 1, character, length, tokenTypes[type], staticModifier >= 0 && (modifiers & (1 << staticModifier)) != 0));
		}

		return tokens;
	}
}
