using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class SemanticTokenRequests(RoslynLanguageClient client)
{
	private string[] _tokenTypes = [];

	internal void ReadLegend(JsonNode? legend) {
		if (legend?["tokenTypes"] is JsonArray types) _tokenTypes = [.. types.Select(entry => entry?.GetValue<string>() ?? string.Empty)];
	}

	public async Task<IReadOnlyList<ServerToken>> FullAsync(string path) {
		if (_tokenTypes.Length == 0 || !client.Documents.IsOpen(path)) return [];

		JsonNode? result = await client.RequestAsync("textDocument/semanticTokens/full", new JsonObject {
			["textDocument"] = client.Contexts.Identifier(path),
		}).ConfigureAwait(false);

		if (result?["data"] is not JsonArray data || data.Count < 5) return [];

		List<ServerToken> tokens = [with(data.Count / 5)];
		int line = 0;
		int character = 0;

		for (int i = 0; i + 4 < data.Count; i += 5) {
			int lineDelta = data[i]?.GetValue<int>() ?? 0;
			int startDelta = data[i + 1]?.GetValue<int>() ?? 0;
			int length = data[i + 2]?.GetValue<int>() ?? 0;
			int type = data[i + 3]?.GetValue<int>() ?? 0;

			line += lineDelta;
			character = lineDelta == 0 ? character + startDelta : startDelta;

			if (length > 0 && type >= 0 && type < _tokenTypes.Length) tokens.Add(new ServerToken(line + 1, character, length, _tokenTypes[type]));
		}

		return tokens;
	}
}
