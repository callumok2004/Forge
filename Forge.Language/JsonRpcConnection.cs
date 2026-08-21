using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;

using Forge.Core.Native;

namespace Forge.Language;

public sealed class JsonRpcConnection : IDisposable
{
	private readonly Process _process;

	private readonly Stream _output;
	private readonly Stream _input;

	private string? _firstLine;
	private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = [];
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
			if (_disposed) return;

			int code = _process.ExitCode;
			Log?.Invoke($"The language server exited with code {code}.");
			FailPending($"The language server exited with code {code}.");
		};

		ThreadPool.UnsafeQueueUserWorkItem(_ => ReadLoop(), null);
		ThreadPool.UnsafeQueueUserWorkItem(_ => WriteLoop(), null);
	}

	private (Stream Input, Stream Output) ConnectTransport() {
		string? line = _process.StandardOutput.ReadLine();

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
		return (_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
	}

	public event Action<string>? Log;

	public event Action<string, JsonNode?>? Notification;

	public Func<string, JsonNode?, JsonNode?>? RequestHandler { get; set; }

	public bool IsRunning => !_disposed && !_process.HasExited;

	public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? parameters,
		CancellationToken cancellationToken = default) {
		int id;
		TaskCompletionSource<JsonNode?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

			completion.TrySetCanceled(cancellationToken);
		});

		return await completion.Task.ConfigureAwait(false);
	}

	public void SendNotification(string method, JsonNode? parameters) =>
		Write(new JsonObject {
			["jsonrpc"] = "2.0",
			["method"] = method,
			["params"] = parameters,
		});

	private void Write(JsonObject message) {
		if (_disposed) return;

		try {
			_outbound.Add(Encoding.UTF8.GetBytes(message.ToJsonString()));
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

				byte[] body = new byte[length];
				int read = 0;

				while (read < length) {
					int got = input.Read(body, read, length - read);

					if (got <= 0) return;

					read += got;
				}

				Dispatch(Encoding.UTF8.GetString(body));
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

	private void Dispatch(string json) {
		JsonNode? message;

		try {
			message = JsonNode.Parse(json);
		}
		catch (JsonException exception) {
			Log?.Invoke($"Malformed message from the language server: {exception.Message}");
			return;
		}

		if (message is not JsonObject root) return;

		string? method = root["method"]?.GetValue<string>();

		if (method is null) {
			CompleteResponse(root);
			return;
		}

		if (root["id"] is { } requestId) {
			JsonNode? result = RequestHandler?.Invoke(method, root["params"]);

			Write(new JsonObject {
				["jsonrpc"] = "2.0",
				["id"] = requestId.DeepClone(),
				["result"] = result,
			});

			return;
		}

		Notification?.Invoke(method, root["params"]);
	}

	private void CompleteResponse(JsonObject root) {
		if (root["id"]?.GetValue<int>() is not { } id) return;

		TaskCompletionSource<JsonNode?>? completion;

		lock (_sync) {
			if (!_pending.Remove(id, out completion)) return;
		}

		if (root["error"] is JsonObject error) {
			completion.TrySetException(new InvalidOperationException(
				error["message"]?.GetValue<string>() ?? "The language server reported an error."));

			return;
		}

		completion.TrySetResult(root["result"]?.DeepClone());
	}

	private void FailPending(string reason) {
		lock (_sync) {
			foreach (TaskCompletionSource<JsonNode?> completion in _pending.Values) {
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
			if (!_process.HasExited) _process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException
			or System.ComponentModel.Win32Exception) {
		}

		if (_input is System.IO.Pipes.NamedPipeClientStream pipe) pipe.Dispose();

		_process.Dispose();
		FailPending("The language server was shut down.");
	}
}
