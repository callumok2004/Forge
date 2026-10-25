using System.Text.Json.Nodes;

using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Server.Features;

namespace Forge.Extensions.Dotnet.Language;

public sealed class CodeActionRunner(RoslynLanguageClient server, DotnetOutput output)
{
	private const string NestedCommand = "roslyn.client.nestedCodeAction";
	private const string FixAllCommand = "roslyn.client.fixAllCodeAction";
	private const string PeekReferencesCommand = "roslyn.client.peekReferences";

	private static readonly Dictionary<string, string> ScopeLabels = new(StringComparer.Ordinal) {
		["Document"] = "Document",
		["Project"] = "Project",
		["Solution"] = "Solution",
		["ContainingMember"] = "Containing Member",
		["ContainingType"] = "Containing Type",
	};

	public ICommandRegistry? Commands { get; set; }

	public async Task ExecuteAsync(ICommandHost host, int index) {
		if (server.CodeActions.Action(index) is { } action) await RunAsync(host, action).ConfigureAwait(false);
	}

	private async Task RunAsync(ICommandHost host, JsonNode action) {
		JsonNode resolved = action;

		if (action["edit"] is null && action["command"] is null && action["data"] is not null) resolved = await server.CodeActions.ResolveAsync(action).ConfigureAwait(false) ?? action;

		if (WorkspaceEditReader.Read(resolved["edit"]) is { IsEmpty: false } edit && !await host.ApplyWorkspaceEditAsync(edit).ConfigureAwait(false)) {
			output.Log(host, $"Code action '{resolved["title"]}' could not be applied.");
			return;
		}

		if (resolved["command"] is JsonObject command) await CommandAsync(host, command).ConfigureAwait(false);
	}

	private async Task CommandAsync(ICommandHost host, JsonObject command) {
		string id = command["command"]?.GetValue<string>() ?? string.Empty;
		JsonArray arguments = command["arguments"] as JsonArray ?? [];
		JsonNode? first = arguments.Count > 0 ? arguments[0] : null;

		switch (id) {
			case NestedCommand when first is not null:
				Nested(host, first, command["title"]?.GetValue<string>() ?? string.Empty);
				return;

			case FixAllCommand when first is not null:
				FixAll(host, first, null);
				return;

			case PeekReferencesCommand:
				return;
		}

		if (Commands?.Find(id) is not null) {
			Commands.Execute(id, host);
			return;
		}

		await server.Workspace.ExecuteAsync(new ServerCommand(command["title"]?.GetValue<string>() ?? id, id, arguments.DeepClone() as JsonArray)).ConfigureAwait(false);
	}

	private static JsonNode DataOf(JsonNode action) => action["data"] ?? action;

	private void Nested(ICommandHost host, JsonNode action, string title) {
		if (DataOf(action)["NestedCodeActions"] is not JsonArray nested || nested.Count == 0) return;

		List<QuickPickItem> items = [with(nested.Count)];

		foreach (JsonNode? child in nested) {
			JsonNode? data = child is null ? null : DataOf(child);
			JsonArray? path = data?["CodeActionPath"] as JsonArray;
			string label = path is { Count: > 1 } ? string.Join(" -> ", path.Skip(1).Select(static part => part?.GetValue<string>())) : child?["title"]?.GetValue<string>() ?? string.Empty;

			items.Add(new QuickPickItem(data?["FixAllFlavors"] is JsonArray { Count: > 0 } ? $"Fix All: {label}" : label));
		}

		host.ShowQuickPick(title.Length > 0 ? title : "Select a fix", items, index => {
			if (index < 0 || nested[index] is not { } chosen) return;

			if (DataOf(chosen)["FixAllFlavors"] is JsonArray { Count: > 0 }) FixAll(host, chosen, null);
			else _ = RunAsync(host, chosen.DeepClone());
		});
	}

	private void FixAll(ICommandHost host, JsonNode action, string? scope) {
		JsonNode data = DataOf(action);

		if (data["FixAllFlavors"] is not JsonArray flavors || flavors.Count == 0) return;

		string title = action["title"]?.GetValue<string>() ?? data["UniqueIdentifier"]?.GetValue<string>() ?? "Fix All";
		List<string> scopes = [.. flavors.Select(static flavor => flavor?.GetValue<string>() ?? string.Empty)];

		if (scope is null && scopes.Count > 1) {
			host.ShowQuickPick("Pick a fix all scope", [.. scopes.Select(static value => new QuickPickItem(ScopeLabels.GetValueOrDefault(value, value)))], index => {
				if (index >= 0) FixAll(host, action, scopes[index]);
			});
			return;
		}

		string chosen = scope ?? scopes[0];

		_ = Task.Run(async () => {
			JsonNode? resolved = await server.CodeActions.ResolveFixAllAsync(title, chosen, data).ConfigureAwait(false);

			if (WorkspaceEditReader.Read(resolved?["edit"]) is { IsEmpty: false } edit) await host.ApplyWorkspaceEditAsync(edit).ConfigureAwait(false);
			else output.Log(host, $"Fix All ({chosen}) produced no changes.");
		});
	}
}
