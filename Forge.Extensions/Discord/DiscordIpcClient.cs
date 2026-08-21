using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text;

using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Discord;

public sealed class DiscordActivityPayload
{
	public string? Name { get; set; }

	public string? Details { get; set; }

	public string? State { get; set; }

	public long? StartTimestamp { get; set; }

	public string? LargeImageKey { get; set; }

	public string? LargeImageText { get; set; }

	public string? SmallImageKey { get; set; }

	public string? SmallImageText { get; set; }

	public (string Label, string Url)? Button { get; set; }
}

public sealed class DiscordIpcClient(string clientId = DiscordIpcClient.ClientId) : IDisposable
{
	private const int OpHandshake = 0;
	private const int OpFrame = 1;
	private const int OpClose = 2;
	private const int OpPing = 3;
	private const int OpPong = 4;

	public const string ClientId = "383226320970055681";

	public const string FakeEmpty = "​​";

	private readonly string _clientId = clientId;
	private NamedPipeClientStream? _pipe;
	private volatile bool _connected;

	public event Action? Ready;

	public event Action? Disconnected;

	public bool IsConnected => _connected;

	public bool Login() {
		for (int id = 0; id < 10; id++) {
			try {
				NamedPipeClientStream pipe = new(".", $"discord-ipc-{id}", PipeDirection.InOut,
					PipeOptions.Asynchronous);

				pipe.Connect(200);
				_pipe = pipe;
				break;
			}
			catch (Exception exception) when (exception is TimeoutException or IOException
				or UnauthorizedAccessException) {
			}
		}

		if (_pipe is null) return false;

		Send(OpHandshake, $"{{\"v\":1,\"client_id\":\"{_clientId}\"}}");

		ThreadPool.UnsafeQueueUserWorkItem(_ => ReadLoop(), null);
		return true;
	}

	private void ReadLoop() {
		byte[] buffer = new byte[8192];
		List<byte> pending = [];

		Span<byte> header = stackalloc byte[8];

		try {
			while (_pipe is { IsConnected: true }) {
				int read = _pipe.Read(buffer, 0, buffer.Length);

				if (read <= 0) break;

				pending.AddRange(buffer.AsSpan(0, read));

				while (pending.Count >= 8) {
					for (int i = 0; i < 8; i++) header[i] = pending[i];

					int opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
					int length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);

					if (length < 0 || pending.Count < 8 + length) break;

					byte[] body = new byte[length];
					pending.CopyTo(8, body, 0, length);
					pending.RemoveRange(0, 8 + length);

					Handle(opcode, Encoding.UTF8.GetString(body));
				}
			}
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException) {
		}

		Close();
	}

	private void Handle(int opcode, string body) {
		if (opcode == OpPing) {
			Send(OpPong, body);
			return;
		}

		if (opcode == OpClose) {
			Close();
			return;
		}

		try {
			using JsonDocument document = JsonDocument.Parse(body);

			if (document.RootElement.TryGetProperty("evt", out JsonElement evt) &&
				evt.GetString() == "READY") {
				_connected = true;
				Ready?.Invoke();
			}
		}
		catch (JsonException) {
		}
	}

	public void SetActivity(DiscordActivityPayload activity) {
		if (!_connected) return;

		Send(OpFrame, BuildActivityFrame(activity));
	}

	public void ClearActivity() {
		if (!_connected) return;

		Send(OpFrame, $"{{\"cmd\":\"SET_ACTIVITY\",\"args\":{{\"pid\":{Environment.ProcessId}}}," +
			$"\"nonce\":\"{Guid.NewGuid()}\"}}");
	}

	private static string BuildActivityFrame(DiscordActivityPayload activity) {
		using MemoryStream stream = new();
		using (Utf8JsonWriter writer = new(stream)) {
			writer.WriteStartObject();
			writer.WriteString("cmd", "SET_ACTIVITY");

			writer.WriteStartObject("args");
			writer.WriteNumber("pid", Environment.ProcessId);

			writer.WriteStartObject("activity");
			writer.WriteNumber("type", 0);

			WriteIfPresent(writer, "name", activity.Name);
			WriteIfPresent(writer, "details", activity.Details);
			WriteIfPresent(writer, "state", activity.State);

			if (activity.StartTimestamp is { } start) {
				writer.WriteStartObject("timestamps");
				writer.WriteNumber("start", start);
				writer.WriteEndObject();
			}

			if (activity.LargeImageKey is not null || activity.SmallImageKey is not null) {
				writer.WriteStartObject("assets");
				WriteIfPresent(writer, "large_image", activity.LargeImageKey);
				WriteIfPresent(writer, "large_text", activity.LargeImageText);
				WriteIfPresent(writer, "small_image", activity.SmallImageKey);
				WriteIfPresent(writer, "small_text", activity.SmallImageText);
				writer.WriteEndObject();
			}

			if (activity.Button is { } button) {
				writer.WriteStartArray("buttons");
				writer.WriteStartObject();
				writer.WriteString("label", button.Label);
				writer.WriteString("url", button.Url);
				writer.WriteEndObject();
				writer.WriteEndArray();
			}

			writer.WriteEndObject();
			writer.WriteEndObject();

			writer.WriteString("nonce", Guid.NewGuid().ToString());
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value) {
		if (value is { Length: > 0 }) writer.WriteString(name, value);
	}

	private void Send(int opcode, string json) {
		if (_pipe is not { IsConnected: true } pipe) return;

		try {
			byte[] body = Encoding.UTF8.GetBytes(json);
			byte[] frame = new byte[8 + body.Length];

			BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
			BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
			body.CopyTo(frame, 8);

			pipe.Write(frame, 0, frame.Length);
			pipe.Flush();
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException) {
			Close();
		}
	}

	private void Close() {
		if (_pipe is null && !_connected) return;

		_connected = false;
		_pipe?.Dispose();
		_pipe = null;

		Disconnected?.Invoke();
	}

	public void Dispose() {
		_connected = false;
		_pipe?.Dispose();
		_pipe = null;
	}
}
