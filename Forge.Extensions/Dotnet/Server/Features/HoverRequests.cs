using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class HoverRequests(RoslynLanguageClient client)
{
	public async Task<string?> RequestAsync(string path, int line, int column) {
		JsonNode? result = await client.RequestAsync("textDocument/hover", client.DocumentAt(path, line, column)).ConfigureAwait(false);

		return result is null ? null : LspJson.Markup(result["contents"]) is { Length: > 0 } text ? text : null;
	}
}
