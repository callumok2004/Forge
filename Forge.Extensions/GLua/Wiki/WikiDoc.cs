using System.Text.Json;

namespace Forge.Extensions.GLua.Wiki;

[Flags]
public enum WikiFlags : ushort
{
	None = 0,
	Client = 1 << 0,
	Menu = 1 << 1,
	Server = 1 << 2,
	New = 1 << 3,
	Deprecated = 1 << 4,
	Internal = 1 << 5,
	ReferenceOnly = 1 << 6,
	Predicted = 1 << 7,
	Event = 1 << 8,
	Method = 1 << 9,
	Function = 1 << 10,
	HasClient = 1 << 11,
	HasMenu = 1 << 12,
	HasServer = 1 << 13
}

public readonly struct WikiText(WikiSource? source, int start, int length)
{
	public bool IsEmpty => source is null;

	public string Value => source?.Decode(start, length) ?? string.Empty;
}

public sealed class WikiSource(byte[] bytes)
{
	private readonly Dictionary<long, string> _decoded = [];
	private readonly Lock _sync = new();

	public string Decode(int start, int length) {
		long key = ((long)start << 32) | (uint)length;

		lock (_sync) {
			if (_decoded.TryGetValue(key, out string? cached)) return cached;
		}

		Utf8JsonReader reader = new(bytes.AsSpan(start, length));
		reader.Read();
		string value = reader.GetString() ?? string.Empty;

		lock (_sync) _decoded[key] = value;

		return value;
	}
}

public sealed class WikiBug
{
	public string? Issue { get; init; }

	public string? Pull { get; init; }

	public WikiText Description { get; init; }
}

public sealed class WikiDoc
{
	public string? Search { get; set; }

	public string? Tag { get; set; }

	public WikiFlags Flags { get; set; }

	public string? Link { get; init; }

	public string? Value { get; init; }

	public string? Family { get; init; }

	public string? Enum { get; init; }

	public string? Type { get; init; }

	public string? Name { get; init; }

	public string? Default { get; init; }

	public string? NetworkVar { get; init; }

	public string? Parent { get; init; }

	public string? RawImage { get; init; }

	public WikiText Description { get; init; }

	public WikiText BaseDescription { get; init; }

	public WikiText[]? Notes { get; init; }

	public WikiText[]? Warnings { get; init; }

	public WikiBug[]? Bugs { get; init; }

	public WikiDoc[]? Arguments { get; init; }

	public WikiDoc[]? Callback { get; init; }

	public WikiDoc[]? Returns { get; init; }

	public (string Path, string Lines)? Source { get; init; }

	public Dictionary<string, WikiDoc>? Members { get; init; }

	public bool Has(WikiFlags flag) => (Flags & flag) != 0;

	public bool HasRealm => (Flags & (WikiFlags.HasClient | WikiFlags.HasMenu | WikiFlags.HasServer)) != 0;
}
