using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

using Forge.Language;

namespace Forge.Tests;

public sealed class JsonRpcConnectionTests : IDisposable
{
	private readonly AnonymousPipeServerStream _toClient = new(PipeDirection.Out);
	private readonly AnonymousPipeServerStream _fromClient = new(PipeDirection.In);
	private readonly JsonRpcConnection _connection;

	public JsonRpcConnectionTests() {
		_connection = new JsonRpcConnection(new AnonymousPipeClientStream(PipeDirection.In, _toClient.ClientSafePipeHandle), new AnonymousPipeClientStream(PipeDirection.Out, _fromClient.ClientSafePipeHandle));
	}

	public void Dispose() {
		_connection.Dispose();
		_toClient.Dispose();
		_fromClient.Dispose();
	}

	private void Send(string json) {
		byte[] body = Encoding.UTF8.GetBytes(json);
		_toClient.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
		_toClient.Write(body);
		_toClient.Flush();
	}

	private JsonObject Receive() {
		int length = -1;
		StringBuilder line = new();

		while (true) {
			int value = _fromClient.ReadByte();

			if (value < 0) throw new EndOfStreamException();

			if (value == '\r') continue;

			if (value != '\n') {
				line.Append((char)value);
				continue;
			}

			if (line.Length == 0) break;

			if (line.ToString().StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.ToString()["Content-Length:".Length..].Trim());

			line.Clear();
		}

		byte[] body = new byte[length];
		int read = 0;

		while (read < length) read += _fromClient.Read(body, read, length - read);

		return (JsonObject)JsonNode.Parse(body)!;
	}

	private void Respond(int id, string result) => Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}");

	private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

	[Fact]
	public async Task RequestRoundTripsResultAsAnIndependentMutableNode() {
		Task<JsonNode?> pending = _connection.SendRequestAsync("test/echo", new JsonObject { ["value"] = "héllo ✓" });
		JsonObject request = Receive();

		Assert.Equal("test/echo", request["method"]!.GetValue<string>());
		Assert.Equal("héllo ✓", request["params"]!["value"]!.GetValue<string>());

		Respond(request["id"]!.GetValue<int>(), """{"items":[{"label":"a","kind":3},{"label":"b"}],"flag":true}""");
		JsonObject result = (JsonObject)(await Within(pending))!;

		Assert.Equal(2, result["items"]!.AsArray().Count);
		Assert.Equal("a", result["items"]![0]!["label"]!.GetValue<string>());
		Assert.True(result["flag"]!.GetValue<bool>());

		result["added"] = 1;
		result["items"]!.AsArray().RemoveAt(0);
		Assert.Single(result["items"]!.AsArray());
	}

	[Fact]
	public async Task ElementRequestReturnsRawResult() {
		Task<System.Text.Json.JsonElement?> pending = _connection.SendRequestElementAsync("test/tokens", null);
		JsonObject request = Receive();
		Respond(request["id"]!.GetValue<int>(), """{"data":[0,1,2,3,4]}""");

		System.Text.Json.JsonElement result = (await Within(pending))!.Value;

		Assert.Equal(5, result.GetProperty("data").GetArrayLength());
	}

	[Fact]
	public async Task NullAndScalarResults() {
		Task<JsonNode?> nullResult = _connection.SendRequestAsync("a", null);
		Respond(Receive()["id"]!.GetValue<int>(), "null");
		Assert.Null(await Within(nullResult));

		Task<JsonNode?> scalar = _connection.SendRequestAsync("b", null);
		Respond(Receive()["id"]!.GetValue<int>(), "42");
		Assert.Equal(42, (await Within(scalar))!.GetValue<int>());
	}

	[Fact]
	public async Task ErrorResponseFaultsTheRequest() {
		Task<JsonNode?> pending = _connection.SendRequestAsync("test/fail", null);
		Send("{\"jsonrpc\":\"2.0\",\"id\":" + Receive()["id"]!.GetValue<int>() + ",\"error\":{\"code\":-32601,\"message\":\"nope\"}}");

		InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Within(pending));
		Assert.Equal("nope", error.Message);
	}

	[Fact]
	public async Task NotificationsCarryParams() {
		TaskCompletionSource<(string, JsonNode?)> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_connection.Notification += (method, parameters) => received.TrySetResult((method, parameters));

		Send("""{"jsonrpc":"2.0","method":"window/logMessage","params":{"message":"hi","nested":{"deep":[1,2]}}}""");
		(string method, JsonNode? parameters) = await Within(received.Task);

		Assert.Equal("window/logMessage", method);
		Assert.Equal("hi", parameters!["message"]!.GetValue<string>());
		Assert.Equal(2, parameters["nested"]!["deep"]!.AsArray().Count);
	}

	[Fact]
	public void ServerRequestsAreAnsweredWithTheSameId() {
		_connection.RequestHandler = (method, parameters) => method == "workspace/configuration" ? new JsonArray("x", parameters?["items"]?.AsArray().Count) : null;

		Send("""{"jsonrpc":"2.0","id":"abc-1","method":"workspace/configuration","params":{"items":[{},{}]}}""");
		JsonObject reply = Receive();

		Assert.Equal("abc-1", reply["id"]!.GetValue<string>());
		Assert.Equal("x", reply["result"]![0]!.GetValue<string>());
		Assert.Equal(2, reply["result"]![1]!.GetValue<int>());

		Send("""{"jsonrpc":"2.0","id":7,"method":"unknown/request"}""");
		JsonObject unknown = Receive();

		Assert.Equal(7, unknown["id"]!.GetValue<int>());
		Assert.Null(unknown["result"]);
	}

	[Fact]
	public async Task AsyncServerRequestsReplyWhenDone() {
		TaskCompletionSource<JsonNode?> answer = new();
		_connection.AsyncRequestHandler = (method, _) => method == "workspace/applyEdit" ? answer.Task : null;

		Send("""{"jsonrpc":"2.0","id":9,"method":"workspace/applyEdit","params":{}}""");
		await Task.Delay(50);
		answer.SetResult(new JsonObject { ["applied"] = true });
		JsonObject reply = Receive();

		Assert.Equal(9, reply["id"]!.GetValue<int>());
		Assert.True(reply["result"]!["applied"]!.GetValue<bool>());
	}

	[Fact]
	public async Task LoneSurrogatesAreRepairedInsteadOfDroppingTheMessage() {
		Task<JsonNode?> pending = _connection.SendRequestAsync("test/surrogate", null);
		int id = Receive()["id"]!.GetValue<int>();
		Respond(id, """{"text":"a\ud800b","pair":"😀"}""");

		JsonNode result = (await Within(pending))!;

		Assert.Equal("a�b", result["text"]!.GetValue<string>());
		Assert.Equal("\U0001F600", result["pair"]!.GetValue<string>());
	}

	[Fact]
	public async Task DeeplyNestedMessagesParse() {
		Task<JsonNode?> pending = _connection.SendRequestAsync("test/deep", null);
		int id = Receive()["id"]!.GetValue<int>();
		Respond(id, new string('[', 1000) + new string(']', 1000));

		Assert.NotNull(await Within(pending));
	}

	[Fact]
	public async Task LargeMessagesAndManyInFlight() {
		Task<JsonNode?>[] pending = [.. Enumerable.Range(0, 50).Select(i => _connection.SendRequestAsync("test/many", new JsonObject { ["i"] = i }))];
		List<int> ids = [.. Enumerable.Range(0, 50).Select(_ => Receive()["id"]!.GetValue<int>())];
		string big = new('x', 3_000_000);

		foreach (int id in Enumerable.Reverse(ids)) Respond(id, "\"" + (id == ids[0] ? big : id.ToString()) + "\"");

		JsonNode?[] results = await Within(Task.WhenAll(pending));

		Assert.Equal(big.Length, results[0]!.GetValue<string>().Length);
		Assert.Equal(ids[49].ToString(), results[49]!.GetValue<string>());
	}
}
