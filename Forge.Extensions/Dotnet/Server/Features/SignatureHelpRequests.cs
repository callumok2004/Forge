using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class SignatureHelpRequests(RoslynLanguageClient client)
{
	public async Task<ServerSignatures?> RequestAsync(string path, int line, int column, char trigger) {
		JsonObject parameters = client.DocumentAt(path, line, column);

		parameters["context"] = trigger == '\0'
			? new JsonObject { ["triggerKind"] = 1, ["isRetrigger"] = false }
			: new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = trigger.ToString(), ["isRetrigger"] = false };

		JsonNode? result = await client.RequestAsync("textDocument/signatureHelp", parameters).ConfigureAwait(false);

		if (result?["signatures"] is not JsonArray signatures || signatures.Count == 0) return null;

		int active = result["activeSignature"]?.GetValue<int>() ?? 0;
		int activeParameter = result["activeParameter"]?.GetValue<int>() ?? 0;
		List<ServerSignature> overloads = [with(signatures.Count)];

		foreach (JsonNode? entry in signatures) {
			if (entry?["label"]?.GetValue<string>() is not { } label) continue;

			List<(int Start, int Length)> parameterRanges = [];

			foreach (JsonNode? parameter in entry["parameters"] as JsonArray ?? []) {
				if (parameter?["label"] is JsonArray offsets && offsets.Count == 2) {
					int start = offsets[0]?.GetValue<int>() ?? 0;
					parameterRanges.Add((start, (offsets[1]?.GetValue<int>() ?? start) - start));
				}
				else if (parameter?["label"]?.GetValue<string>() is { Length: > 0 } text && label.IndexOf(text, StringComparison.Ordinal) is >= 0 and int at) parameterRanges.Add((at, text.Length));
				else parameterRanges.Add((0, 0));
			}

			overloads.Add(new ServerSignature(label, parameterRanges, LspJson.Markup(entry["documentation"]), entry["activeParameter"]?.GetValue<int>() ?? -1));
		}

		return overloads.Count == 0 ? null : new ServerSignatures(overloads, Math.Clamp(active, 0, overloads.Count - 1), Math.Max(0, activeParameter));
	}
}
