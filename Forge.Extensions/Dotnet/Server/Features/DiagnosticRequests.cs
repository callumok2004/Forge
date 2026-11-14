using System.Text.Json.Nodes;

using Forge.Common.Commands;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class DiagnosticRequests(RoslynLanguageClient client)
{
	private readonly Dictionary<string, string> _raw = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, string> _resultIds = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, CancellationTokenSource> _inflight = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, (string ResultId, string Items)> _contextResults = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	public event Action<string, IReadOnlyList<ServerDiagnostic>>? Published;

	public Func<ProjectContext, Task<IReadOnlySet<string>>>? DefinesFor { get; set; }

	internal JsonArray Overlapping(string path, int startLine, int endLine) {
		JsonArray result = [];
		string? stored;

		lock (_sync) _raw.TryGetValue(path, out stored);

		if (stored is null || JsonNode.Parse(stored) is not JsonArray diagnostics) return result;

		for (int i = diagnostics.Count - 1; i >= 0; i--) {
			JsonNode? entry = diagnostics[i];
			int first = LspJson.Line(entry?["range"]?["start"]);
			int last = LspJson.Line(entry?["range"]?["end"]);

			if (last < startLine || first > endLine) continue;

			diagnostics.RemoveAt(i);
			result.Insert(0, entry);
		}

		return result;
	}

	private List<ServerDiagnostic> Store(string path, JsonArray diagnostics) {
		string compact = diagnostics.ToJsonString();

		lock (_sync) _raw[path] = compact;

		return Read(path, diagnostics);
	}

	public async Task PullAsync(string path) {
		if (!client.Documents.IsOpen(path)) return;

		CancellationTokenSource current = new();
		string? previousResultId;

		lock (_sync) {
			if (_inflight.Remove(path, out CancellationTokenSource? superseded)) superseded.Cancel();

			_inflight[path] = current;
			_resultIds.TryGetValue(path, out previousResultId);
		}

		if (DefinesFor is { } definesFor && client.Contexts.For(path) is { } contexts && contexts.All.Count(static context => !context.IsMiscellaneous) > 1) {
			try {
				await PullContextsAsync(path, contexts, definesFor, current.Token).ConfigureAwait(false);
			}
			finally {
				lock (_sync) {
					if (_inflight.TryGetValue(path, out CancellationTokenSource? active) && active == current) _inflight.Remove(path);

					_resultIds.Remove(path);
				}

				current.Dispose();
			}

			return;
		}

		JsonObject parameters = new() { ["textDocument"] = client.Contexts.Identifier(path) };

		if (previousResultId is not null) parameters["previousResultId"] = previousResultId;

		JsonNode? result;

		try {
			result = await client.RequestAsync("textDocument/diagnostic", parameters, current.Token).ConfigureAwait(false);
		}
		finally {
			lock (_sync) {
				if (_inflight.TryGetValue(path, out CancellationTokenSource? active) && active == current) _inflight.Remove(path);
			}

			current.Dispose();
		}

		if (result?["resultId"]?.GetValue<string>() is { } resultId) {
			lock (_sync) _resultIds[path] = resultId;
		}

		if (result?["kind"]?.GetValue<string>() == "unchanged" || result?["items"] is not JsonArray items) return;

		Published?.Invoke(path, Store(path, items));
	}

	private async Task<JsonArray?> PullContextAsync(string path, ProjectContext context, CancellationToken cancellation) {
		string key = $"{path}|{context.Id}";
		JsonObject parameters = new() { ["textDocument"] = client.Contexts.Identifier(path, context) };
		(string ResultId, string Items) cached;

		lock (_sync) _contextResults.TryGetValue(key, out cached);

		if (cached.ResultId is not null) parameters["previousResultId"] = cached.ResultId;

		JsonNode? result = await client.RequestAsync("textDocument/diagnostic", parameters, cancellation).ConfigureAwait(false);

		if (cancellation.IsCancellationRequested || result is null) return null;

		if (result["kind"]?.GetValue<string>() == "unchanged") return cached.Items is null ? null : JsonNode.Parse(cached.Items) as JsonArray;

		if (result["items"] is not JsonArray items) return null;

		if (result["resultId"]?.GetValue<string>() is { } resultId) {
			string compact = items.ToJsonString();

			lock (_sync) _contextResults[key] = (resultId, compact);
		}

		return items;
	}

	private async Task PullContextsAsync(string path, ProjectContexts contexts, Func<ProjectContext, Task<IReadOnlySet<string>>> definesFor, CancellationToken cancellation) {
		string? current = contexts.Current?.Id;
		JsonArray merged = [];
		HashSet<string> seen = [with(StringComparer.Ordinal)];

		ProjectContext[] targets = [.. contexts.All.Where(static context => !context.IsMiscellaneous)];
		JsonArray?[] results = await Task.WhenAll(targets.Select(context => PullContextAsync(path, context, cancellation))).ConfigureAwait(false);

		if (cancellation.IsCancellationRequested) return;

		for (int index = 0; index < targets.Length; index++) {
			ProjectContext context = targets[index];

			if (results[index] is not { } items) continue;

			IReadOnlySet<string> defined = await definesFor(context).ConfigureAwait(false);
			string? text = client.Documents.Text(path);
			bool[]? active = defined.Count == 0 || text is null ? null : Language.Preprocessor.ActiveLines(text, defined);

			foreach (JsonNode? entry in items) {
				if (entry is null) continue;

				int line = LspJson.Line(entry["range"]?["start"]) - 1;
				bool compiled = active is null ? context.Id == current : (uint)line >= (uint)active.Length || active[line];

				if (!compiled) continue;

				string identity = $"{entry["range"]?.ToJsonString()}|{entry["severity"]}|{entry["code"]}|{entry["message"]}";

				if (seen.Add(identity)) merged.Add(entry.DeepClone());
			}
		}

		if (cancellation.IsCancellationRequested) return;

		Published?.Invoke(path, Store(path, merged));
	}

	internal void Forget(string path) {
		lock (_sync) {
			_resultIds.Remove(path);
			_raw.Remove(path);

			foreach (string key in _contextResults.Keys.Where(key => key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList()) _contextResults.Remove(key);

			if (_inflight.Remove(path, out CancellationTokenSource? pending)) pending.Cancel();
		}
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

			if (severity > 3) continue;

			string message = entry["message"]?.GetValue<string>() ?? string.Empty;
			string? code = entry["code"]?.ToString();

			results.Add(new ServerDiagnostic(path, LspJson.Line(start), LspJson.Column(start), message, severity switch { 1 => DiagnosticSeverity.Error, 2 => DiagnosticSeverity.Warning, _ => DiagnosticSeverity.Info }, code is { Length: > 0 } ? code : null, LspJson.Line(entry["range"]?["end"]), LspJson.Column(entry["range"]?["end"])));
		}

		return results;
	}
}
