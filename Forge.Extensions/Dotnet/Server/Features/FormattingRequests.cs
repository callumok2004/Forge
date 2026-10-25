using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class FormattingRequests(RoslynLanguageClient client)
{
	public async Task<IReadOnlyList<ServerTextEdit>> DocumentAsync(string path, int tabSize, bool insertSpaces) {
		JsonNode? result = await client.RequestAsync("textDocument/formatting", new JsonObject {
			["textDocument"] = client.Contexts.Identifier(path),
			["options"] = new JsonObject {
				["tabSize"] = tabSize,
				["insertSpaces"] = insertSpaces,
			},
		}).ConfigureAwait(false);

		List<ServerTextEdit> edits = LspJson.Edits(result);

		edits.Sort(static (left, right) => right.StartLine != left.StartLine ? right.StartLine.CompareTo(left.StartLine) : right.StartColumn.CompareTo(left.StartColumn));

		return edits;
	}
}
