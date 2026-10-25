using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public readonly record struct ServerHighlight(int Line, int StartColumn, int EndLine, int EndColumn, int Kind);

public sealed class HighlightRequests(RoslynLanguageClient client)
{
	public async Task<IReadOnlyList<ServerHighlight>> RequestAsync(string path, int line, int column) {
		JsonNode? result = await client.RequestAsync("textDocument/documentHighlight", client.DocumentAt(path, line, column)).ConfigureAwait(false);
		List<ServerHighlight> highlights = [];

		foreach (JsonNode? entry in result as JsonArray ?? []) {
			if (entry?["range"]?["start"] is not { } start || entry["range"]?["end"] is not { } end) continue;

			highlights.Add(new ServerHighlight(LspJson.Line(start), LspJson.Column(start), LspJson.Line(end), LspJson.Column(end), entry["kind"]?.GetValue<int>() ?? 1));
		}

		return highlights;
	}
}
