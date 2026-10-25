using System.Text.Json.Nodes;

using Forge.Common.Commands;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class RenameRequests(RoslynLanguageClient client)
{
	public async Task<RenameTarget?> PrepareAsync(string path, int line, int column) {
		JsonNode? result = await client.RequestAsync("textDocument/prepareRename", client.DocumentAt(path, line, column)).ConfigureAwait(false);

		JsonNode? range = result?["range"] ?? (result?["start"] is not null ? result : null);

		if (range?["start"] is not { } start || range["end"] is not { } end) return null;

		int startLine = LspJson.Line(start);
		int startColumn = LspJson.Column(start);
		int endColumn = LspJson.Line(end) == startLine ? LspJson.Column(end) : startColumn;
		string placeholder = result?["placeholder"]?.GetValue<string>() ?? string.Empty;

		return new RenameTarget(startLine, startColumn, endColumn, placeholder);
	}

	public async Task<WorkspaceEdit?> RenameAsync(string path, int line, int column, string newName) {
		JsonObject parameters = client.DocumentAt(path, line, column);

		parameters["newName"] = newName;

		return WorkspaceEditReader.Read(await client.RequestAsync("textDocument/rename", parameters).ConfigureAwait(false));
	}
}
