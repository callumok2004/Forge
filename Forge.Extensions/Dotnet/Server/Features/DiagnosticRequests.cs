using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class DiagnosticRequests(RoslynLanguageClient client)
{
	private readonly Dictionary<string, JsonArray> _raw = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	public event Action<string, IReadOnlyList<ServerDiagnostic>>? Published;

	internal JsonArray Overlapping(string path, int startLine, int endLine) {
		JsonArray result = [];

		lock (_sync) {
			if (!_raw.TryGetValue(path, out JsonArray? diagnostics)) return result;

			foreach (JsonNode? entry in diagnostics) {
				int first = LspJson.Line(entry?["range"]?["start"]);
				int last = LspJson.Line(entry?["range"]?["end"]);

				if (last >= startLine && first <= endLine) result.Add(entry!.DeepClone());
			}
		}

		return result;
	}

	private List<ServerDiagnostic> Store(string path, JsonArray diagnostics) {
		lock (_sync) _raw[path] = (JsonArray)diagnostics.DeepClone();

		return Read(path, diagnostics);
	}

	public async Task PullAsync(string path) {
		if (!client.Documents.IsOpen(path)) return;

		JsonNode? result = await client.RequestAsync("textDocument/diagnostic", new JsonObject {
			["textDocument"] = client.Contexts.Identifier(path),
		}).ConfigureAwait(false);

		if (result?["kind"]?.GetValue<string>() == "unchanged" || result?["items"] is not JsonArray items) return;

		Published?.Invoke(path, Store(path, items));
	}

	internal void OnPublished(JsonNode? parameters) {
		if (parameters?["uri"]?.GetValue<string>() is not { } uri || SourceGeneratedDocuments.IsGenerated(uri) || parameters["diagnostics"] is not JsonArray diagnostics) return;

		string path = RoslynLanguageClient.FromUri(uri);
		Published?.Invoke(path, Store(path, diagnostics));
	}

	private static List<ServerDiagnostic> Read(string path, JsonArray diagnostics) {
		List<ServerDiagnostic> results = [with(diagnostics.Count)];

		foreach (JsonNode? entry in diagnostics) {
			if (entry?["range"]?["start"] is not { } start) continue;

			int severity = entry["severity"]?.GetValue<int>() ?? 1;

			if (severity > 2) continue;

			string message = entry["message"]?.GetValue<string>() ?? string.Empty;
			string? code = entry["code"]?.ToString();

			results.Add(new ServerDiagnostic(path, LspJson.Line(start), LspJson.Column(start), code is { Length: > 0 } ? $"{code}: {message}" : message, severity == 1));
		}

		return results;
	}
}
