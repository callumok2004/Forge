using System.Text.Json.Nodes;

using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Server.Features;

namespace Forge.Extensions.Dotnet.Server;

internal static class WorkspaceEditReader
{
	public static WorkspaceEdit? Read(JsonNode? edit) {
		if (edit is not JsonObject root) return null;

		List<WorkspaceOperation> operations = [];

		if (root["documentChanges"] is JsonArray documentChanges) {
			foreach (JsonNode? change in documentChanges) {
				if (Operation(change) is { } operation) operations.Add(operation);
			}
		}
		else if (root["changes"] is JsonObject changes) {
			foreach ((string uri, JsonNode? edits) in changes) {
				if (Path(uri) is { } path) operations.Add(new WorkspaceOperation(WorkspaceOperationKind.Edit, path, Edits(edits)));
			}
		}

		return new WorkspaceEdit(operations);
	}

	private static WorkspaceOperation? Operation(JsonNode? change) {
		JsonNode? options = change?["options"];
		bool overwrite = options?["overwrite"]?.GetValue<bool>() ?? false;
		bool ignoreIfExists = options?["ignoreIfExists"]?.GetValue<bool>() ?? false;

		return change?["kind"]?.GetValue<string>() switch {
			"create" when Path(change["uri"]) is { } path => new WorkspaceOperation(WorkspaceOperationKind.Create, path, [], Overwrite: overwrite, IgnoreIfExists: ignoreIfExists),
			"rename" when Path(change["oldUri"]) is { } path && Path(change["newUri"]) is { } target => new WorkspaceOperation(WorkspaceOperationKind.Rename, path, [], target, overwrite, ignoreIfExists),
			"delete" when Path(change["uri"]) is { } path => new WorkspaceOperation(WorkspaceOperationKind.Delete, path, [], IgnoreIfNotExists: options?["ignoreIfNotExists"]?.GetValue<bool>() ?? false, Recursive: options?["recursive"]?.GetValue<bool>() ?? false),
			null when Path(change?["textDocument"]?["uri"]) is { } path => new WorkspaceOperation(WorkspaceOperationKind.Edit, path, Edits(change?["edits"])),
			_ => null,
		};
	}

	private static string? Path(JsonNode? uri) => uri?.GetValue<string>() is { } text ? Path(text) : null;

	private static string? Path(string uri) => SourceGeneratedDocuments.IsGenerated(uri) ? null : RoslynLanguageClient.FromUri(uri);

	private static List<HostTextEdit> Edits(JsonNode? edits) => [.. LspJson.Edits(edits).Select(static edit => new HostTextEdit(edit.StartLine, edit.StartColumn, edit.EndLine, edit.EndColumn, edit.NewText))];
}
