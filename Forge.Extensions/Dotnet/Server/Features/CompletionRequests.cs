using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class CompletionRequests(RoslynLanguageClient client)
{
	private static readonly string[] KindNames = ["Text", "Text", "Method", "Function", "Constructor", "Field", "Variable", "Class", "Interface", "Module", "Property", "Unit", "Value", "Enum", "Keyword", "Snippet", "Color", "File", "Reference", "Folder", "EnumMember", "Constant", "Struct", "Event", "Operator", "TypeParameter"];

	private JsonElement _items;
	private JsonElement? _defaults;

	public async Task<ServerCompletionList> RequestAsync(string path, int line, int column, char trigger, int triggerKind) {
		JsonObject parameters = client.DocumentAt(path, line, column);

		parameters["context"] = triggerKind == 2 && trigger != '\0'
			? new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = trigger.ToString() }
			: new JsonObject { ["triggerKind"] = triggerKind == 3 ? 3 : 1 };

		JsonElement? result = await client.RequestElementAsync("textDocument/completion", parameters).ConfigureAwait(false);
		JsonElement? items = result is { ValueKind: JsonValueKind.Array } array ? array : result.Property("items");

		_defaults = result.Property("itemDefaults");
		_items = items is { ValueKind: JsonValueKind.Array } list ? list : default;

		if (_items.ValueKind != JsonValueKind.Array) return new ServerCompletionList([], false);

		List<ServerCompletion> completions = Read(_items, _defaults, line, column);

		return new ServerCompletionList(completions, result.Property("isIncomplete").Bool() ?? false);
	}

	internal static List<ServerCompletion> Read(JsonElement items, JsonElement? defaultsElement, int line, int column) {
		JsonElement? defaults = defaultsElement.Property("editRange");
		JsonElement? defaultRange = defaults.Property("replace") ?? defaults;
		List<ServerCompletion> completions = [with(items.GetArrayLength())];
		int index = -1;

		foreach (JsonElement item in items.EnumerateArray()) {
			index++;

			if (item.Property("label").String() is not { Length: > 0 } label) continue;

			JsonElement? edit = item.Property("textEdit");
			JsonElement? range = edit.Property("range") ?? edit.Property("replace") ?? defaultRange;
			JsonElement? labelDetails = item.Property("labelDetails");
			JsonElement? start = range.Property("start");
			bool hasRange = start.Property("character") is not null;
			int kind = item.Property("kind").Int() ?? 0;

			completions.Add(new ServerCompletion(
				Index: index,
				Label: label,
				LabelDetail: labelDetails.Property("detail").String() ?? string.Empty,
				LabelDescription: labelDetails.Property("description").String() ?? string.Empty,
				InsertText: edit.Property("newText").String() ?? item.Property("insertText").String() ?? label,
				Detail: item.Property("detail").String() ?? string.Empty,
				Kind: (uint)kind < (uint)KindNames.Length ? KindNames[kind] : "Text",
				SortText: item.Property("sortText").String() ?? label,
				FilterText: item.Property("filterText").String() ?? label,
				Preselect: item.Property("preselect").Bool() ?? false,
				ReplaceLine: start is not null ? LspJson.Line(start) : line,
				ReplaceStartColumn: hasRange ? LspJson.Column(start) : 0,
				ReplaceEndColumn: hasRange ? (range.Property("end") is { } end ? LspJson.Column(end) : column) : 0));
		}

		completions.Sort(static (left, right) => {
			int order = string.CompareOrdinal(left.SortText, right.SortText);
			return order != 0 ? order : string.CompareOrdinal(left.Label, right.Label);
		});

		return completions;
	}

	public async Task<ServerCompletionDetail?> ResolveAsync(int index) {
		if (_items.ValueKind != JsonValueKind.Array || (uint)index >= (uint)_items.GetArrayLength()) return null;

		if (JsonObject.Create(_items[index]) is not { } request) return null;

		if (_defaults.Property("data") is { } sharedData && request["data"] is null) request["data"] = JsonRpcValue(sharedData);

		JsonNode? result = await client.RequestAsync("completionItem/resolve", request).ConfigureAwait(false);

		return result is null ? null : new ServerCompletionDetail(result["detail"]?.GetValue<string>() ?? string.Empty, LspJson.Markup(result["documentation"]), LspJson.Edits(result["additionalTextEdits"]));
	}

	private static JsonNode? JsonRpcValue(JsonElement element) => Forge.Language.JsonRpcConnection.ToNode(element);
}
