using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server.Features;

public sealed class DocumentSync(RoslynLanguageClient client)
{
	private readonly Dictionary<string, int> _versions = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, string> _texts = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	private bool _ready;

	private static string Normalize(string path) {
		try {
			string full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
			return full is [>= 'a' and <= 'z', ':', ..] ? char.ToUpperInvariant(full[0]) + full[1..] : full;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
			return path;
		}
	}

	public bool IsOpen(string path) {
		path = Normalize(path);

		lock (_sync) return _ready && _versions.ContainsKey(path);
	}

	public string[] OpenPaths() {
		lock (_sync) return [.. _texts.Keys];
	}

	public void Open(string path, string text) {
		path = Normalize(path);

		if (client.Generated.UriFor(path) is not null) return;

		bool alreadyOpen;

		lock (_sync) {
			alreadyOpen = _ready && _versions.ContainsKey(path);

			if (!alreadyOpen) {
				_texts[path] = text;

				if (!_ready) return;

				_versions[path] = 1;
			}
		}

		if (alreadyOpen) {
			Change(path, text);
			return;
		}

		SendOpen(path, text);
	}

	public void Change(string path, string text) {
		path = Normalize(path);

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
				["uri"] = UriOf(path),
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
		path = Normalize(path);

		bool wasOpen;

		lock (_sync) {
			_texts.Remove(path);
			wasOpen = _versions.Remove(path) && _ready;
		}

		if (wasOpen) SendClose(path);
	}

	public void Save(string path) {
		path = Normalize(path);

		if (IsOpen(path)) client.Notify("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) } });
	}

	internal void Ready() {
		lock (_sync) {
			if (_ready) return;

			_ready = true;
			_versions.Clear();

			foreach ((string path, string text) in _texts) {
				_versions[path] = 1;
				SendOpen(path, text);
			}
		}
	}

	internal void Reset() {
		lock (_sync) {
			_ready = false;
			_versions.Clear();
		}

		lock (_wire) _serverOpen.Clear();
	}

	private readonly Dictionary<string, string> _serverOpen = [with(StringComparer.OrdinalIgnoreCase)];

	private string UriOf(string path) {
		lock (_wire) return _serverOpen.TryGetValue(path, out string? uri) ? uri : RoslynLanguageClient.ToUri(path);
	}

	private readonly Lock _wire = new();
	private int _resync = 1000;

	private void SendOpen(string path, string text) {
		lock (_wire) {
			if (_serverOpen.TryGetValue(path, out string? existing)) {
				string uri = existing;
				client.Notify("textDocument/didChange", new JsonObject {
					["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = Interlocked.Increment(ref _resync) },
					["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
				});
				return;
			}

			string opened = RoslynLanguageClient.ToUri(path);
			_serverOpen[path] = opened;
			client.Notify("textDocument/didOpen", new JsonObject {
				["textDocument"] = new JsonObject {
					["uri"] = opened,
					["languageId"] = "csharp",
					["version"] = 1,
					["text"] = text,
				},
			});
		}
	}

	private void SendClose(string path) {
		lock (_wire) {
			if (_serverOpen.Remove(path, out string? uri)) client.Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
		}
	}

	private static JsonObject PositionOf(string text, int offset) {
		ReadOnlySpan<char> before = text.AsSpan(0, Math.Min(offset, text.Length));
		int lastBreak = before.LastIndexOf('\n');

		return new JsonObject {
			["line"] = before.Count('\n'),
			["character"] = offset - (lastBreak + 1),
		};
	}
}
