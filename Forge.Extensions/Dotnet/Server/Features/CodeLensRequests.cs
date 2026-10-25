using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public readonly record struct ServerCodeLens(int Index, int Line, int Column, string? Title);

public readonly record struct ServerLensCommand(string Title, string? Command, JsonArray? Arguments);

public sealed class CodeLensRequests(RoslynLanguageClient client)
{
	private readonly ConcurrentDictionary<string, JsonNode?[]> _lenses = new(StringComparer.OrdinalIgnoreCase);

	public async Task<IReadOnlyList<ServerCodeLens>> RequestAsync(string path) {
		if (!client.Documents.IsOpen(path)) return [];

		JsonNode? result = await client.RequestAsync("textDocument/codeLens", new JsonObject {
			["textDocument"] = client.Contexts.Identifier(path),
		}).ConfigureAwait(false);

		JsonArray items = result as JsonArray ?? [];
		List<ServerCodeLens> lenses = [with(items.Count)];
		JsonNode?[] stored = [.. items];

		_lenses[path] = stored;

		for (int i = 0; i < stored.Length; i++) {
			if (stored[i]?["range"]?["start"] is not { } start) continue;

			lenses.Add(new ServerCodeLens(i, LspJson.Line(start), LspJson.Column(start), stored[i]?["command"]?["title"]?.GetValue<string>()));
		}

		return lenses;
	}

	public async Task<ServerLensCommand?> ResolveAsync(string path, int index) {
		if (!_lenses.TryGetValue(path, out JsonNode?[]? stored) || (uint)index >= (uint)stored.Length || stored[index] is not { } lens) return null;

		JsonNode? resolved = lens["command"] is null ? await client.RequestAsync("codeLens/resolve", lens.DeepClone().AsObject()).ConfigureAwait(false) : lens;

		if (resolved?["command"] is not { } command || command["title"]?.GetValue<string>() is not { } title) return null;

		stored[index] = resolved;

		return new ServerLensCommand(title, command["command"]?.GetValue<string>(), command["arguments"]?.DeepClone() as JsonArray);
	}

	private int _version;

	public int Version => Volatile.Read(ref _version);

	public event Action? Changed;

	public void Invalidate() {
		Interlocked.Increment(ref _version);
		Changed?.Invoke();
	}
}
