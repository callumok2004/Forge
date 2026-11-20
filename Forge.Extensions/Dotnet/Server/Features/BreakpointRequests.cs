using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class BreakpointRequests(RoslynLanguageClient client)
{
	public async Task<BreakableLine> BreakableLineAsync(string path, int line) {
		if (client.Connection is not { } connection) return new BreakableLine(true, line);

		JsonObject parameters = new() {
			["_vs_textDocument"] = client.Contexts.Identifier(path),
			["_vs_range"] = new JsonObject { ["start"] = LspJson.Position(line, 1), ["end"] = LspJson.Position(line, 1) },
		};

		try {
			JsonNode? result = await connection.SendRequestAsync("textDocument/_vs_validateBreakableRange", parameters).ConfigureAwait(false);

			return result?["start"]?["line"]?.GetValue<int>() is { } start ? new BreakableLine(true, start + 1) : new BreakableLine(false, line);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or OperationCanceledException) {
			return new BreakableLine(true, line);
		}
	}
}

public readonly record struct BreakableLine(bool Valid, int Line);
