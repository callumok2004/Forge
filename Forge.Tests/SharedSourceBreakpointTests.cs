using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Forge.Tests;

public sealed class SharedSourceBreakpointTests(SharedSourceBreakpointTests.Fixture fixture) : IClassFixture<SharedSourceBreakpointTests.Fixture>
{
	private const string Shared = """
		namespace Shared;

		public static class Util
		{
		    public static int Twice(int x)
		    {
		        int y = x * 2;
		        return y;
		    }

		#if LIBB
		    public static int OnlyB(int x)
		    {
		        return x + 1;
		    }
		#endif
		    public static int Both(int x)
		    {
		        return x - 1;
		    }
		}
		""";

	private const string Program = """
		extern alias A;
		extern alias B;
		System.Console.WriteLine(A::Shared.Util.Twice(1));
		System.Console.WriteLine(A::Shared.Util.Both(1));
		Next();
		static void Next()
		{
		    System.Console.WriteLine(B::Shared.Util.Twice(2));
		    System.Console.WriteLine(B::Shared.Util.OnlyB(3));
		    System.Console.WriteLine(A::Shared.Util.Both(5));
		}
		""";

	private static readonly string Adapter = Path.Combine(AppContext.BaseDirectory, "Debugger", "dncdbg", "dncdbg.exe");

	private async Task<(List<(string Method, int Line)> Hits, int ReportedLine)> RunAsync(int line) {
		Assert.True(File.Exists(Adapter), Adapter);
		await fixture.Built;
		string root = fixture.Root;
		string sharedFile = Path.Combine(root, "Shared", "Shared.cs");

		await using DapClient client = new(Adapter);
		await client.RequestAsync("initialize", new JsonObject { ["adapterID"] = "coreclr", ["linesStartAt1"] = true, ["columnsStartAt1"] = true });
		string program = Path.Combine(root, "App", "bin", "Debug", "net11.0", "App.dll");
		await client.SendAsync("launch", new JsonObject { ["program"] = "dotnet", ["args"] = new JsonArray(program), ["cwd"] = root, ["justMyCode"] = true });
		await client.EventAsync("initialized");
		JsonNode breakpoints = await client.RequestAsync("setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = sharedFile }, ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = line }) });
		int id = breakpoints["body"]!["breakpoints"]![0]!["id"]!.GetValue<int>();
		await client.RequestAsync("configurationDone", []);

		List<(string, int)> hits = [];

		while (await client.EventAsync("stopped", "terminated", "exited") is { } stopped && stopped["event"]!.GetValue<string>() == "stopped") {
			int thread = stopped["body"]!["threadId"]!.GetValue<int>();
			JsonNode trace = await client.RequestAsync("stackTrace", new JsonObject { ["threadId"] = thread, ["levels"] = 1 });
			JsonNode frame = trace["body"]!["stackFrames"]![0]!;
			hits.Add((frame["name"]!.GetValue<string>(), frame["line"]!.GetValue<int>()));
			await client.RequestAsync("continue", new JsonObject { ["threadId"] = thread });
		}

		return (hits, client.LastBreakpointLine(id));
	}

	[Fact]
	public async Task LineInBothModulesHitsInBoth() {
		(List<(string Method, int Line)> hits, _) = await RunAsync(7);

		Assert.Equal([("Shared.Util.Twice(int x)", 7), ("Shared.Util.Twice(int x)", 7)], hits);
	}

	[Fact]
	public async Task LineInOneModuleNeverMovesInTheOther() {
		(List<(string Method, int Line)> hits, int reported) = await RunAsync(14);

		Assert.Equal([("Shared.Util.OnlyB(int x)", 14)], hits);
		Assert.Equal(14, reported);
	}

	[Fact]
	public async Task LineOutsideMethodsIsNeverMoved() {
		(List<(string Method, int Line)> hits, int reported) = await RunAsync(10);

		Assert.Empty(hits);
		Assert.Equal(0, reported);
	}

	[Fact]
	public async Task ModuleLoadedFirstWithoutCodeAtLineGetsNoBinding() {
		(List<(string Method, int Line)> hits, int reported) = await RunAsync(13);

		Assert.Equal([("Shared.Util.OnlyB(int x)", 13)], hits);
		Assert.Equal(13, reported);
	}

	public sealed class Fixture : IDisposable
	{
		public string Root { get; } = Path.Combine(Path.GetTempPath(), "forge-shared-bp", Guid.NewGuid().ToString("N"));

		public Task Built { get; }

		public Fixture() {
			Directory.CreateDirectory(Path.Combine(Root, "Shared"));
			File.WriteAllText(Path.Combine(Root, "Shared", "Shared.cs"), Shared.ReplaceLineEndings("\n"));

			foreach (string library in (string[])["LibA", "LibB"]) {
				string defines = library == "LibB" ? "<DefineConstants>$(DefineConstants);LIBB</DefineConstants>" : "";
				Directory.CreateDirectory(Path.Combine(Root, library));
				File.WriteAllText(Path.Combine(Root, library, library + ".csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework>{defines}</PropertyGroup><ItemGroup><Compile Include=\"../Shared/Shared.cs\" /></ItemGroup></Project>");
			}

			Directory.CreateDirectory(Path.Combine(Root, "App"));
			File.WriteAllText(Path.Combine(Root, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../LibA/LibA.csproj\" Aliases=\"A\" /><ProjectReference Include=\"../LibB/LibB.csproj\" Aliases=\"B\" /></ItemGroup></Project>");
			File.WriteAllText(Path.Combine(Root, "App", "Program.cs"), Program);
			Built = BuildAsync();
		}

		private async Task BuildAsync() {
			using Process build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{Path.Combine(Root, "App", "App.csproj")}\" -nologo -v:q") { RedirectStandardOutput = true })!;
			string output = await build.StandardOutput.ReadToEndAsync();
			await build.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
			Assert.True(build.ExitCode == 0, output);
		}

		public void Dispose() {
			try {
				Directory.Delete(Root, true);
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException) {
			}
		}
	}

	private sealed class DapClient : IAsyncDisposable
	{
		private readonly Process _process;
		private readonly Stream _input;
		private readonly Stream _output;
		private readonly Dictionary<int, int> _breakpointLines = [];
		private readonly Queue<JsonNode> _pending = [];
		private int _seq;

		public DapClient(string adapter) {
			_process = Process.Start(new ProcessStartInfo(adapter, "--interpreter=vscode") { RedirectStandardInput = true, RedirectStandardOutput = true })!;
			_input = _process.StandardInput.BaseStream;
			_output = _process.StandardOutput.BaseStream;
		}

		public int LastBreakpointLine(int id) => _breakpointLines.GetValueOrDefault(id);

		public async Task<int> SendAsync(string command, JsonObject arguments) {
			int seq = ++_seq;
			byte[] body = Encoding.UTF8.GetBytes(new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command, ["arguments"] = arguments }.ToJsonString());
			await _input.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
			await _input.WriteAsync(body);
			await _input.FlushAsync();
			return seq;
		}

		public async Task<JsonNode> RequestAsync(string command, JsonObject arguments) {
			int seq = await SendAsync(command, arguments);

			while (true) {
				JsonNode message = await ReadAsync();

				if (message["type"]!.GetValue<string>() == "response" && message["request_seq"]!.GetValue<int>() == seq) return message;

				_pending.Enqueue(message);
			}
		}

		public async Task<JsonNode?> EventAsync(params string[] names) {
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

			while (true) {
				JsonNode message = _pending.Count > 0 ? _pending.Dequeue() : await ReadAsync().WaitAsync(timeout.Token);

				if (message["type"]!.GetValue<string>() == "event" && names.Contains(message["event"]!.GetValue<string>())) return message;
			}
		}

		private async Task<JsonNode> ReadAsync() {
			StringBuilder header = new();

			while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) {
				int value = _output.ReadByte();

				if (value < 0) throw new EndOfStreamException();

				header.Append((char)value);
			}

			int length = int.Parse(header.ToString().Split("Content-Length:")[1].Split("\r\n")[0].Trim());
			byte[] body = new byte[length];
			await _output.ReadExactlyAsync(body);
			JsonNode message = JsonNode.Parse(body)!;

			if (message["event"]?.GetValue<string>() == "breakpoint" && message["body"]!["breakpoint"] is { } breakpoint && breakpoint["line"] is { } line) _breakpointLines[breakpoint["id"]!.GetValue<int>()] = line.GetValue<int>();

			return message;
		}

		public async ValueTask DisposeAsync() {
			try {
				_process.Kill(true);
				await _process.WaitForExitAsync();
			}
			catch (InvalidOperationException) {
			}

			_process.Dispose();
		}
	}
}
