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

		return Sorted(LspJson.Edits(result));
	}

	public async Task<IReadOnlyList<ServerTextEdit>> RangeAsync(string path, int startLine, int startColumn, int endLine, int endColumn, int tabSize, bool insertSpaces) {
		JsonNode? result = await client.RequestAsync("textDocument/rangeFormatting", new JsonObject {
			["textDocument"] = client.Contexts.Identifier(path),
			["range"] = new JsonObject {
				["start"] = LspJson.Position(startLine, startColumn),
				["end"] = LspJson.Position(endLine, endColumn),
			},
			["options"] = new JsonObject {
				["tabSize"] = tabSize,
				["insertSpaces"] = insertSpaces,
			},
		}).ConfigureAwait(false);

		return Sorted(LspJson.Edits(result));
	}

	private static List<ServerTextEdit> Sorted(List<ServerTextEdit> edits) {
		edits.Sort(static (left, right) => right.StartLine != left.StartLine ? right.StartLine.CompareTo(left.StartLine) : right.StartColumn.CompareTo(left.StartColumn));

		return edits;
	}
}
