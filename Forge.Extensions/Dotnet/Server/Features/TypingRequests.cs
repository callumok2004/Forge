using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed record ServerTriggers(string Suggest, string Signature, string SignatureRetrigger, string OnTypeFormatting, string AutoInsert);

public readonly record struct ServerAutoInsert(ServerTextEdit Edit, bool IsSnippet);

public sealed class TypingRequests(RoslynLanguageClient client)
{
	public ServerTriggers Triggers { get; private set; } = new(".", "(,", ")", string.Empty, string.Empty);

	internal void ReadCapabilities(JsonObject served) => Triggers = new ServerTriggers(
		Characters(served["completionProvider"]?["triggerCharacters"]),
		Characters(served["signatureHelpProvider"]?["triggerCharacters"]),
		Characters(served["signatureHelpProvider"]?["retriggerCharacters"]),
		served["documentOnTypeFormattingProvider"] is { } onType ? (onType["firstTriggerCharacter"]?.GetValue<string>() ?? string.Empty) + Characters(onType["moreTriggerCharacter"]) : string.Empty,
		Characters(served["_vs_onAutoInsertProvider"]?["_vs_triggerCharacters"]));

	private static string Characters(JsonNode? array) {
		string result = string.Empty;

		foreach (JsonNode? entry in array as JsonArray ?? []) {
			if (entry?.GetValue<string>() is { Length: 1 } character) result += character;
		}

		return result;
	}

	public async Task<IReadOnlyList<ServerTextEdit>> OnTypeFormattingAsync(string path, int line, int column, char character, int tabSize, bool insertSpaces) {
		JsonObject parameters = client.DocumentAt(path, line, column);

		parameters["ch"] = character.ToString();
		parameters["options"] = new JsonObject { ["tabSize"] = tabSize, ["insertSpaces"] = insertSpaces };

		return LspJson.Edits(await client.RequestAsync("textDocument/onTypeFormatting", parameters).ConfigureAwait(false));
	}

	public async Task<ServerAutoInsert?> AutoInsertAsync(string path, int line, int column, char character, int tabSize, bool insertSpaces) {
		JsonObject parameters = new() {
			["_vs_textDocument"] = client.Contexts.Identifier(path),
			["_vs_position"] = LspJson.Position(line, column),
			["_vs_ch"] = character.ToString(),
			["_vs_options"] = new JsonObject { ["tabSize"] = tabSize, ["insertSpaces"] = insertSpaces },
		};

		JsonNode? result = await client.RequestAsync("textDocument/_vs_onAutoInsert", parameters).ConfigureAwait(false);

		if (LspJson.Edit(result?["_vs_textEdit"]) is not { } edit) return null;

		return new ServerAutoInsert(edit, result?["_vs_textEditFormat"]?.GetValue<int>() == 2);
	}
}
