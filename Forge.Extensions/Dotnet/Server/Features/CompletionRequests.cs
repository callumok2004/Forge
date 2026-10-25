using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class CompletionRequests(RoslynLanguageClient client)
{
	private static readonly string[] KindNames = ["Text", "Text", "Method", "Function", "Constructor", "Field", "Variable", "Class", "Interface", "Module", "Property", "Unit", "Value", "Enum", "Keyword", "Snippet", "Color", "File", "Reference", "Folder", "EnumMember", "Constant", "Struct", "Event", "Operator", "TypeParameter"];

	private JsonNode?[] _items = [];
	private JsonObject? _defaults;

	public async Task<ServerCompletionList> RequestAsync(string path, int line, int column, char trigger, int triggerKind) {
		JsonObject parameters = client.DocumentAt(path, line, column);

		parameters["context"] = triggerKind == 2 && trigger != '\0'
			? new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = trigger.ToString() }
			: new JsonObject { ["triggerKind"] = triggerKind == 3 ? 3 : 1 };

		JsonNode? result = await client.RequestAsync("textDocument/completion", parameters).ConfigureAwait(false);
		JsonArray items = (result is JsonArray array ? array : result?["items"] as JsonArray) ?? [];

		_defaults = result?["itemDefaults"] as JsonObject;
		_items = [.. items];

		JsonNode? defaultRange = _defaults?["editRange"]?["replace"] ?? _defaults?["editRange"];
		List<ServerCompletion> completions = [with(items.Count)];

		for (int i = 0; i < items.Count; i++) {
			JsonNode? item = items[i];

			if (item?["label"]?.GetValue<string>() is not { Length: > 0 } label) continue;

			JsonNode? edit = item["textEdit"];
			JsonNode? range = edit?["range"] ?? edit?["replace"] ?? defaultRange;
			JsonNode? labelDetails = item["labelDetails"];
			bool hasRange = range?["start"]?["character"] is not null;
			int kind = item["kind"]?.GetValue<int>() ?? 0;

			completions.Add(new ServerCompletion(
				Index: i,
				Label: label,
				LabelDetail: labelDetails?["detail"]?.GetValue<string>() ?? string.Empty,
				LabelDescription: labelDetails?["description"]?.GetValue<string>() ?? string.Empty,
				InsertText: edit?["newText"]?.GetValue<string>() ?? item["insertText"]?.GetValue<string>() ?? label,
				Detail: item["detail"]?.GetValue<string>() ?? string.Empty,
				Kind: (uint)kind < (uint)KindNames.Length ? KindNames[kind] : "Text",
				SortText: item["sortText"]?.GetValue<string>() ?? label,
				FilterText: item["filterText"]?.GetValue<string>() ?? label,
				Preselect: item["preselect"]?.GetValue<bool>() ?? false,
				ReplaceLine: range?["start"] is { } start ? LspJson.Line(start) : line,
				ReplaceStartColumn: hasRange ? LspJson.Column(range!["start"]) : 0,
				ReplaceEndColumn: hasRange ? (range!["end"] is { } end ? LspJson.Column(end) : column) : 0));
		}

		completions.Sort(static (left, right) => {
			int order = string.CompareOrdinal(left.SortText, right.SortText);
			return order != 0 ? order : string.CompareOrdinal(left.Label, right.Label);
		});

		return new ServerCompletionList(completions, result?["isIncomplete"]?.GetValue<bool>() ?? false);
	}

	public async Task<ServerCompletionDetail?> ResolveAsync(int index) {
		if ((uint)index >= (uint)_items.Length || _items[index] is not { } item) return null;

		JsonObject request = item.DeepClone().AsObject();

		if (_defaults?["data"] is { } sharedData && request["data"] is null) request["data"] = sharedData.DeepClone();

		JsonNode? result = await client.RequestAsync("completionItem/resolve", request).ConfigureAwait(false);

		return result is null ? null : new ServerCompletionDetail(result["detail"]?.GetValue<string>() ?? string.Empty, LspJson.Markup(result["documentation"]), LspJson.Edits(result["additionalTextEdits"]));
	}
}
