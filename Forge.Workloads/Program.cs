using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Workloads;

public static class Program
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

	public static async Task<int> Main(string[] args) {
		CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
		Dictionary<string, string> named = Parse(args);

		if (!named.TryGetValue("root", out string? root)) {
			Console.WriteLine("usage: Forge.Workloads --root <folder> [--seed 1] [--files 4 (per project)] [--positions 5] [--keystrokes 40] [--settle 10] [--load-timeout 900] [--sample-ms 250] [--server <dir>] [--out <dir>] [--census 0|1] [--profile 0|1] [--quick 1] [--phases open]");
			return 2;
		}

		if (named.ContainsKey("quick")) {
			named.TryAdd("files", "1");
			named.TryAdd("positions", "2");
			named.TryAdd("keystrokes", "5");
			named.TryAdd("settle", "2");
		}

		WorkloadOptions options = new(root,
			Int(named, "seed", 1), Int(named, "files", 4), Int(named, "positions", 5), Int(named, "keystrokes", 40),
			Int(named, "settle", 10), Int(named, "load-timeout", 900), named.GetValueOrDefault("server"), Int(named, "sample-ms", 250), Int(named, "census", 0) != 0, Int(named, "profile", 1) != 0, named.GetValueOrDefault("phases") == "open");

		WorkloadResult result;

		try {
			result = await new RoslynWorkload(options, Log).RunAsync().ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException) {
			Log($"FAILED: {exception.Message}");
			return 1;
		}

		string output = named.GetValueOrDefault("out") ?? Path.Combine(RepositoryRoot() ?? Environment.CurrentDirectory, "artifacts", "workloads");
		Directory.CreateDirectory(output);
		string name = Path.GetFileName(result.Root.TrimEnd(Path.DirectorySeparatorChar));
		string? previous = Directory.EnumerateFiles(output, $"{name}-*.json").Order(StringComparer.Ordinal).LastOrDefault();
		string file = Path.Combine(output, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
		File.WriteAllText(file, JsonSerializer.Serialize(result, Json));

		Report.Print(result, previous is null ? null : JsonSerializer.Deserialize<WorkloadResult>(File.ReadAllText(previous), Json));
		Log($"Saved {file}");
		return result.SourceChanges.Count == 0 ? 0 : 3;
	}

	private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

	private static int Int(Dictionary<string, string> named, string key, int fallback) => named.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) ? parsed : fallback;

	private static Dictionary<string, string> Parse(string[] args) {
		Dictionary<string, string> named = [with(StringComparer.OrdinalIgnoreCase)];

		for (int i = 0; i + 1 < args.Length; i++) {
			if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;

			named[args[i][2..]] = args[i + 1];
			i++;
		}

		return named;
	}

	private static string? RepositoryRoot() {
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
			if (File.Exists(Path.Combine(directory.FullName, "Forge.slnx"))) return directory.FullName;
		}

		return null;
	}
}
