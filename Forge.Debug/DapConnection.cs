using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Forge.Core.Native;

namespace Forge.Debug;

public sealed class DapConnection : IDisposable
{
	private readonly Process? _process;
	private readonly Stream _input;
	private readonly Stream _output;

	private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponse>> _pending = [];
	private readonly BlockingCollection<byte[]> _outbound = [];

	private int _nextSeq;
	private bool _disposed;

	public DapConnection(ProcessStartInfo startInfo) {
		startInfo.RedirectStandardInput = true;
		startInfo.RedirectStandardOutput = true;
		startInfo.RedirectStandardError = true;
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;

		_process = ProcessGroup.Start(startInfo)
			?? throw new InvalidOperationException($"Could not start the debug adapter {startInfo.FileName}.");

		_input = _process.StandardOutput.BaseStream;
		_output = _process.StandardInput.BaseStream;

		_process.ErrorDataReceived += (_, e) => {
			if (e.Data is { Length: > 0 }) Log?.Invoke(e.Data);
		};

		_process.BeginErrorReadLine();
		_process.EnableRaisingEvents = true;

		_process.Exited += (_, _) => {
			if (_disposed) return;

			Fail($"The debug adapter exited with code {_process.ExitCode}.");
			Exited?.Invoke(_process.ExitCode);
		};

		Start();
	}

	public DapConnection(Stream input, Stream output) {
		_input = input;
		_output = output;
		Start();
	}

	public event Action<string>? Log;

	public event Action<int>? Exited;

	public event Action<string, JsonNode?>? Event;

	public Func<string, JsonNode?, JsonNode?>? RequestHandler { get; set; }

	public bool IsRunning => !_disposed && (_process is null || !_process.HasExited);

	public async Task<JsonNode?> SendAsync(string command, JsonNode? arguments,
		CancellationToken cancellationToken = default) {
		DapResponse response = await RequestAsync(command, arguments, cancellationToken).ConfigureAwait(false);

		if (!response.Success) throw new DapException(command, response.Message);

		return response.Body;
	}

	public async Task<DapResponse> RequestAsync(string command, JsonNode? arguments,
		CancellationToken cancellationToken = default) {
		int seq = Interlocked.Increment(ref _nextSeq);
		TaskCompletionSource<DapResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		_pending[seq] = completion;

		JsonObject message = new() {
			["seq"] = seq,
			["type"] = "request",
			["command"] = command,
		};

		if (arguments is not null) message["arguments"] = arguments;

		Write(message);

		using CancellationTokenRegistration registration = cancellationToken.Register(() => {
			_pending.TryRemove(seq, out _);
			completion.TrySetCanceled(cancellationToken);
		});

		return await completion.Task.ConfigureAwait(false);
	}

	private void Start() {
		ThreadPool.UnsafeQueueUserWorkItem(_ => ReadLoop(), null);
		ThreadPool.UnsafeQueueUserWorkItem(_ => WriteLoop(), null);
	}

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
				_output.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
				_output.Write(body);
				_output.Flush();
			}
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException
			or InvalidOperationException) {
			Log?.Invoke($"Debug adapter write failed: {exception.Message}");
		}
	}

	private void ReadLoop() {
		try {
			while (!_disposed) {
				int length = ReadHeaders(_input);

				if (length < 0) break;

				byte[] body = new byte[length];
				int read = 0;

				while (read < length) {
					int got = _input.Read(body, read, length - read);

					if (got <= 0) return;

					read += got;
				}

				Dispatch(Encoding.UTF8.GetString(body));
			}
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException) {
		}

		Fail("The debug adapter stopped.");
	}

	private static int ReadHeaders(Stream input) {
		int length = -1;
		StringBuilder line = new();

		while (true) {
			int value = input.ReadByte();

			if (value < 0) return -1;

			if (value != '\n') {
				if (value != '\r') line.Append((char)value);

				continue;
			}

			if (line.Length == 0) return length;

			const string header = "Content-Length:";

			if (line.ToString() is { } text &&
				text.StartsWith(header, StringComparison.OrdinalIgnoreCase) &&
				int.TryParse(text.AsSpan(header.Length).Trim(), out int parsed)) length = parsed;

			line.Clear();
		}
	}

	private void Dispatch(string text) {
		JsonNode? message;

		try {
			message = JsonNode.Parse(text);
		}
		catch (JsonException) {
			return;
		}

		switch (message?["type"]?.GetValue<string>()) {
			case "response":
				CompleteResponse(message);
				break;

			case "event": {
				string name = message["event"]?.GetValue<string>() ?? string.Empty;

				try {
					Event?.Invoke(name, message["body"]);
				}
				catch (Exception exception) {
					Log?.Invoke($"event {name} failed: {exception}");
				}

				break;
			}

			case "request":
				HandleReverseRequest(message);
				break;
		}
	}

	private void CompleteResponse(JsonNode message) {
		if (message["request_seq"]?.GetValue<int>() is not { } seq) return;

		if (!_pending.TryRemove(seq, out TaskCompletionSource<DapResponse>? completion)) return;

		completion.TrySetResult(new DapResponse(
			message["success"]?.GetValue<bool>() ?? false,
			message["body"],
			message["message"]?.GetValue<string>()));
	}

	private void HandleReverseRequest(JsonNode message) {
		string command = message["command"]?.GetValue<string>() ?? string.Empty;
		int seq = message["seq"]?.GetValue<int>() ?? 0;

		JsonNode? body = null;
		bool success = true;

		try {
			body = RequestHandler?.Invoke(command, message["arguments"]);
		}
		catch (Exception exception) {
			success = false;
			Log?.Invoke($"{command} failed: {exception}");
		}

		JsonObject response = new() {
			["seq"] = Interlocked.Increment(ref _nextSeq),
			["type"] = "response",
			["request_seq"] = seq,
			["success"] = success,
			["command"] = command,
		};

		if (body is not null) response["body"] = body;

		Write(response);
	}

	private void Fail(string reason) {
		foreach (int seq in _pending.Keys) {
			if (_pending.TryRemove(seq, out TaskCompletionSource<DapResponse>? completion)) {
				completion.TrySetException(new DapException(reason, null));
			}
		}
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		Fail("The debug adapter was shut down.");

		_outbound.CompleteAdding();

		try {
			if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException
			or System.ComponentModel.Win32Exception) {
		}

		_process?.Dispose();
	}
}

public readonly record struct DapResponse(bool Success, JsonNode? Body, string? Message);

public sealed class DapException(string command, string? message)
	: Exception(message is { Length: > 0 } ? $"{command}: {message}" : command);
