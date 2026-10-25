using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class DocumentSync(RoslynLanguageClient client)
{
	private readonly Dictionary<string, int> _versions = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, string> _texts = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	private bool _ready;

	public bool IsOpen(string path) {
		lock (_sync) return _ready && _versions.ContainsKey(path);
	}

	public void Open(string path, string text) {
		if (client.Generated.UriFor(path) is not null) return;

		lock (_sync) {
			_texts[path] = text;

			if (!_ready) return;

			_versions[path] = 1;
		}

		SendOpen(path, text);
	}

	public void Change(string path, string text) {
		if (client.Generated.UriFor(path) is not null) return;

		int version;
		string? previous;

		lock (_sync) {
			_texts.TryGetValue(path, out previous);
			_texts[path] = text;

			if (!_ready) return;

			if (previous is null || !_versions.TryGetValue(path, out int current)) {
				_versions[path] = 1;
				version = 0;
			}
			else {
				version = current + 1;
				_versions[path] = version;
			}
		}

		if (version == 0) {
			SendOpen(path, text);
			return;
		}

		if (previous == text) return;

		int shortest = Math.Min(previous!.Length, text.Length);
		int prefix = previous.AsSpan(0, shortest).CommonPrefixLength(text.AsSpan(0, shortest));
		int suffix = 0;

		while (suffix < shortest - prefix && previous[^(suffix + 1)] == text[^(suffix + 1)]) suffix++;

		client.Notify("textDocument/didChange", new JsonObject {
			["textDocument"] = new JsonObject {
				["uri"] = RoslynLanguageClient.ToUri(path),
				["version"] = version,
			},
			["contentChanges"] = new JsonArray(new JsonObject {
				["range"] = new JsonObject {
					["start"] = PositionOf(previous, prefix),
					["end"] = PositionOf(previous, previous.Length - suffix),
				},
				["text"] = text[prefix..(text.Length - suffix)],
			}),
		});
	}

	public void Close(string path) {
		bool wasOpen;

		lock (_sync) {
			_texts.Remove(path);
			wasOpen = _versions.Remove(path) && _ready;
		}

		if (wasOpen) client.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = RoslynLanguageClient.ToUri(path) } });
	}

	public void Save(string path) {
		if (IsOpen(path)) client.Notify("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = RoslynLanguageClient.ToUri(path) } });
	}

	internal void Ready() {
		List<(string Path, string Text)> open;

		lock (_sync) {
			_ready = true;
			_versions.Clear();
			open = [.. _texts.Select(static pair => (pair.Key, pair.Value))];

			foreach ((string path, _) in open) _versions[path] = 1;
		}

		foreach ((string path, string text) in open) SendOpen(path, text);
	}

	internal void Reset() {
		lock (_sync) {
			_ready = false;
			_versions.Clear();
		}
	}

	private void SendOpen(string path, string text) => client.Notify("textDocument/didOpen", new JsonObject {
		["textDocument"] = new JsonObject {
			["uri"] = RoslynLanguageClient.ToUri(path),
			["languageId"] = "csharp",
			["version"] = 1,
			["text"] = text,
		},
	});

	private static JsonObject PositionOf(string text, int offset) {
		ReadOnlySpan<char> before = text.AsSpan(0, Math.Min(offset, text.Length));
		int lastBreak = before.LastIndexOf('\n');

		return new JsonObject {
			["line"] = before.Count('\n'),
			["character"] = offset - (lastBreak + 1),
		};
	}
}
