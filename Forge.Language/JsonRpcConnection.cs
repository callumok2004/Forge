using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;

using Forge.Core.Native;

namespace Forge.Language;

public sealed class JsonRpcConnection : IDisposable
{
	private readonly Process? _process;

	private readonly Stream _output;
	private readonly Stream _input;

	private string? _firstLine;
	private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = [];
	private readonly Lock _sync = new();

	private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _outbound = [];

	private int _nextId = 1;
	private bool _disposed;

	public JsonRpcConnection(ProcessStartInfo startInfo, bool negotiateTransport = true) {
		startInfo.RedirectStandardInput = true;
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;

		_process = ProcessGroup.Start(startInfo)
			?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");

		(_input, _output) = negotiateTransport
			? ConnectTransport()
			: (_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);

		_process.ErrorDataReceived += (_, e) => {
			if (e.Data is { Length: > 0 }) Log?.Invoke(e.Data);
		};

		_process.BeginErrorReadLine();

		_process.EnableRaisingEvents = true;

		_process.Exited += (_, _) => {
			_exited = true;

			if (_disposed) return;

			int code = _process.ExitCode;
			Log?.Invoke($"The language server exited with code {code}.");
			FailPending($"The language server exited with code {code}.");
			Exited?.Invoke(code);
		};

		ThreadPool.UnsafeQueueUserWorkItem(_ => ReadLoop(), null);
		ThreadPool.UnsafeQueueUserWorkItem(_ => WriteLoop(), null);
	}

	internal JsonRpcConnection(Stream input, Stream output) {
		_input = input;
		_output = output;

		ThreadPool.UnsafeQueueUserWorkItem(_ => ReadLoop(), null);
		ThreadPool.UnsafeQueueUserWorkItem(_ => WriteLoop(), null);
	}

	private (Stream Input, Stream Output) ConnectTransport() {
		string? line = _process!.StandardOutput.ReadLine();

		if (line is { Length: > 0 } && line.TrimStart().StartsWith('{') &&
			JsonNode.Parse(line)?["pipeName"]?.GetValue<string>() is { Length: > 0 } pipeName) {
			string name = pipeName.Contains('\\') || pipeName.Contains('/')
				? Path.GetFileName(pipeName)
				: pipeName;

			System.IO.Pipes.NamedPipeClientStream pipe = new(".", name,
				System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

			pipe.Connect(30_000);
			Log?.Invoke($"Connected to the language server over the named pipe {name}.");

			return (pipe, pipe);
		}

		_firstLine = line;
		return (_process!.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
	}

	public event Action<string>? Log;

	public event Action<int>? Exited;

	public event Action<string, JsonNode?>? Notification;

	public Func<string, JsonNode?, JsonNode?>? RequestHandler { get; set; }

	public Func<string, JsonNode?, Task<JsonNode?>?>? AsyncRequestHandler { get; set; }

	private volatile bool _exited;

	public bool IsRunning => !_disposed && !_exited;

	public int ProcessId => _process?.Id ?? 0;

	public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default) => ToNode(await SendRequestElementAsync(method, parameters, cancellationToken).ConfigureAwait(false));

	public async Task<JsonElement?> SendRequestElementAsync(string method, JsonNode? parameters, CancellationToken cancellationToken = default) {
		int id;
		TaskCompletionSource<JsonElement?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		lock (_sync) {
			id = _nextId++;
			_pending[id] = completion;
		}

		JsonObject message = new() {
			["jsonrpc"] = "2.0",
			["id"] = id,
			["method"] = method,
			["params"] = parameters,
		};

		Write(message);

		using CancellationTokenRegistration registration = cancellationToken.Register(() => {
			lock (_sync) _pending.Remove(id);

			if (completion.TrySetCanceled(cancellationToken) && IsRunning) SendNotification("$/cancelRequest", new JsonObject { ["id"] = id });
		});

		return await completion.Task.ConfigureAwait(false);
	}

	public static JsonNode? ToNode(JsonElement? element) => element is not { } value ? null : value.ValueKind switch {
		JsonValueKind.Object => JsonObject.Create(value),
		JsonValueKind.Array => JsonArray.Create(value),
		JsonValueKind.Null or JsonValueKind.Undefined => null,
		_ => JsonValue.Create(value),
	};

	public void SendNotification(string method, JsonNode? parameters) =>
		Write(new JsonObject {
			["jsonrpc"] = "2.0",
			["method"] = method,
			["params"] = parameters,
		});

	private void Write(JsonObject message) {
		if (_disposed) return;

		try {
			System.Buffers.ArrayBufferWriter<byte> buffer = new(256);

			using (Utf8JsonWriter writer = new(buffer)) message.WriteTo(writer);

			_outbound.Add(buffer.WrittenSpan.ToArray());
		}
		catch (InvalidOperationException) {
		}
	}

	private void WriteLoop() {
		try {
			foreach (byte[] body in _outbound.GetConsumingEnumerable()) {
				byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

				_output.Write(header);
				_output.Write(body);
				_output.Flush();
			}
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException
			or InvalidOperationException) {
			Log?.Invoke($"Language server write failed: {exception.Message}");
		}
	}

	private void ReadLoop() {
		Stream input = _input;

		try {
			while (!_disposed) {
				int length = ReadHeaders(input, Interlocked.Exchange(ref _firstLine, null));

				if (length < 0) break;

				byte[] body = System.Buffers.ArrayPool<byte>.Shared.Rent(length);

				try {
					int read = 0;

					while (read < length) {
						int got = input.Read(body, read, length - read);

						if (got <= 0) return;

						read += got;
					}

					Dispatch(body.AsSpan(0, length));
				}
				catch (Exception exception) when (exception is not (IOException or ObjectDisposedException)) {
					Log?.Invoke($"Failed to handle a {length} byte message: {exception.GetType().Name}: {exception.Message}");
				}
				finally {
					System.Buffers.ArrayPool<byte>.Shared.Return(body);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException) {
		}

		FailPending("The language server stopped.");
	}

	private static int ReadHeaders(Stream input, string? alreadyRead) {
		int length = -1;
		StringBuilder line = new();

		if (alreadyRead is { Length: > 0 }) {
			const string first = "Content-Length:";

			if (alreadyRead.StartsWith(first, StringComparison.OrdinalIgnoreCase) &&
				int.TryParse(alreadyRead.AsSpan(first.Length).Trim(), out int fromFirst)) length = fromFirst;
		}

		while (true) {
			int value = input.ReadByte();

			if (value < 0) return -1;

			if (value != '\n') {
				if (value != '\r') line.Append((char)value);

				continue;
			}

			if (line.Length == 0) return length;

			const string prefix = "Content-Length:";

			if (line.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
				int.TryParse(line.ToString().AsSpan(prefix.Length).Trim(), out int parsed)) length = parsed;

			line.Clear();
		}
	}

	private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 4096 };

	private static bool Escape(string json, int at, out int code) {
		code = 0;

		if (at + 5 >= json.Length || json[at] != '\\' || json[at + 1] != 'u') return false;

		return int.TryParse(json.AsSpan(at + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out code);
	}

	private static string ReplaceLoneSurrogates(string json) {
		StringBuilder builder = new(json.Length);

		for (int i = 0; i < json.Length; i++) {
			if (json[i] != '\\' || i + 1 >= json.Length) {
				builder.Append(json[i]);
				continue;
			}

			if (!Escape(json, i, out int code)) {
				builder.Append(json, i, 2);
				i++;
				continue;
			}

			if (code is >= 0xD800 and <= 0xDBFF && Escape(json, i + 6, out int low) && low is >= 0xDC00 and <= 0xDFFF) {
				builder.Append(json, i, 12);
				i += 11;
				continue;
			}

			builder.Append(code is >= 0xD800 and <= 0xDFFF ? "\\uFFFD" : json.Substring(i, 6));
			i += 5;
		}

		return builder.ToString();
	}

	private JsonElement? Parse(ReadOnlySpan<byte> json) {
		bool surrogates = json.IndexOf("\\ud"u8) >= 0 || json.IndexOf("\\uD"u8) >= 0;

		if (!surrogates) {
			try {
				Utf8JsonReader reader = new(json, new JsonReaderOptions { MaxDepth = ParseOptions.MaxDepth });
				return JsonElement.ParseValue(ref reader);
			}
			catch (JsonException) {
			}
		}

		try {
			using JsonDocument repaired = JsonDocument.Parse(ReplaceLoneSurrogates(Encoding.UTF8.GetString(json)), ParseOptions);
			return repaired.RootElement.Clone();
		}
		catch (JsonException exception) {
			Log?.Invoke($"Malformed message from the language server: {exception.Message}");
			return null;
		}
	}

	private void Dispatch(ReadOnlySpan<byte> json) {
		if (Parse(json) is not { } root) return;

		if (root.ValueKind != JsonValueKind.Object) return;

		if (!root.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String) {
			CompleteResponse(root);
			return;
		}

		string method = methodElement.GetString()!;
		JsonNode? parameters = root.TryGetProperty("params", out JsonElement paramsElement) ? ToNode(paramsElement) : null;

		if (root.TryGetProperty("id", out JsonElement idElement)) {
			JsonNode requestId = JsonValue.Create(idElement)!;

			if (AsyncRequestHandler?.Invoke(method, parameters) is { } pending) {
				_ = ReplyAsync(requestId, pending);
				return;
			}

			Reply(requestId, RequestHandler?.Invoke(method, parameters));
			return;
		}

		Notification?.Invoke(method, parameters);
	}

	private void Reply(JsonNode id, JsonNode? result) => Write(new JsonObject {
		["jsonrpc"] = "2.0",
		["id"] = id,
		["result"] = result,
	});

	private async Task ReplyAsync(JsonNode id, Task<JsonNode?> pending) {
		JsonNode? result = null;

		try {
			result = await pending.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or IOException) {
			Log?.Invoke($"Deferred request failed: {exception.Message}");
		}

		if (!_disposed) Reply(id, result);
	}

	private void CompleteResponse(JsonElement root) {
		if (!root.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt32(out int id)) return;

		TaskCompletionSource<JsonElement?>? completion;

		lock (_sync) {
			if (!_pending.Remove(id, out completion)) return;
		}

		if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object) {
			completion.TrySetException(new InvalidOperationException(error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String ? message.GetString() : "The language server reported an error."));
			return;
		}

		completion.TrySetResult(root.TryGetProperty("result", out JsonElement result) ? result : null);
	}

	private void FailPending(string reason) {
		lock (_sync) {
			foreach (TaskCompletionSource<JsonElement?> completion in _pending.Values) {
				completion.TrySetException(new InvalidOperationException(reason));
			}

			_pending.Clear();
		}
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		_outbound.CompleteAdding();

		try {
			if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException
			or System.ComponentModel.Win32Exception) {
		}

		if (_input is System.IO.Pipes.NamedPipeClientStream pipe) pipe.Dispose();

		_process?.Dispose();

		if (_process is null) {
			_input.Dispose();
			_output.Dispose();
		}
		FailPending("The language server was shut down.");
	}
}
