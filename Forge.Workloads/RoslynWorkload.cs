using System.Text.RegularExpressions;

using Forge.Extensions.Dotnet;
using Forge.Extensions.Dotnet.Language;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Workloads;

internal sealed record WorkloadOptions(string Root, int Seed, int Files, int Positions, int Keystrokes, int SettleSeconds, int LoadTimeoutSeconds, string? ServerPath, int SampleMilliseconds, bool Census, bool Profile, bool OpenOnly);

internal sealed record WorkloadResult(string Root, string Solution, List<string> SourceChanges, WorkloadOptions Options, double LoadSeconds, int FilesOpened, int Diagnostics, List<PhaseResult> Phases, Dictionary<string, Distribution> Latencies, Dictionary<string, List<HeapEntry>> Heaps, List<ServerProfile> Profiles, List<Sample> Samples);

internal sealed partial class RoslynWorkload(WorkloadOptions options, Action<string> log)
{
	[GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]{2,}\b")]
	private static partial Regex Identifier();

	private readonly Latencies _latencies = new();
	private readonly Dictionary<string, List<HeapEntry>> _heaps = [];
	private readonly List<ServerProfile> _profiles = [];
	private ServerProfiler? _profiler;

	public async Task<WorkloadResult> RunAsync() {
		string root = Path.GetFullPath(options.Root);
		Dictionary<string, (long, DateTime)> before = SourceGuard.Capture(root);
		HashSet<string> intermediateBefore = SourceGuard.CaptureIntermediate(root);
		log($"Guarding {before.Count} source files and {intermediateBefore.Count} obj files under {root}");

		SolutionWorkspace workspace = new();
		workspace.Reload(root);

		if (workspace.Target is null) throw new InvalidOperationException($"No solution or project found under {root}.");

		log($"Target {workspace.Target}");

		DotnetSettings settings = new();

		if (Environment.GetEnvironmentVariable("FORGE_WORKLOAD_SERVER_ENV") is { Length: > 0 } overrides) settings.ServerEnvironment = overrides.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(static pair => pair.Split('=', 2)).ToDictionary(static pair => pair[0], static pair => pair.Length > 1 ? pair[1] : string.Empty);
		ServerLaunch launch = LanguageServerLocator.Locate(options.ServerPath ?? DefaultServer(), settings.StorageDirectory, settings.DotnetPath)
			?? throw new InvalidOperationException("No Roslyn language server found; build Forge.App or pass --server.");

		log($"Server {launch.ServerDirectory}");

		using Sampler sampler = new(options.SampleMilliseconds);
		using RoslynLanguageClient client = new();
		ProjectDefines defines = new(workspace, settings);
		client.Diagnostics.DefinesFor = defines.For;

		TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
		client.ProjectsLoaded += () => loaded.TrySetResult();
		client.ProjectsLoaded += defines.Invalidate;
		client.Log += message => {
			if (message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("fail", StringComparison.OrdinalIgnoreCase)) log($"  server: {message}");
		};

		int diagnostics = 0;
		Dictionary<string, TaskCompletionSource> published = [with(StringComparer.OrdinalIgnoreCase)];
		Lock publishedSync = new();

		client.Diagnostics.Published += (path, items) => {
			Interlocked.Add(ref diagnostics, items.Count);

			lock (publishedSync) {
				if (published.Remove(path, out TaskCompletionSource? waiter)) waiter.TrySetResult();
			}
		};

		sampler.Phase = "load";
		double loadStart = sampler.Elapsed;
		string logs = Path.Combine(Path.GetTempPath(), "forge-workloads", "logs");

		if (options.Profile) Environment.SetEnvironmentVariable("DOTNET_ReadyToRun", "0");
		client.Start(launch, logs, workspace.Solution, workspace.Projects, settings);

		if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(options.LoadTimeoutSeconds))).ConfigureAwait(false) != loaded.Task) throw new TimeoutException($"Projects did not load within {options.LoadTimeoutSeconds}s ({client.StatusText}).");

		double loadSeconds = sampler.Elapsed - loadStart;

		if (options.Profile && client.ServerProcessId is { } serverId) _profiler = new ServerProfiler(serverId, log);
		log($"Projects loaded in {loadSeconds:F1}s");

		await Settle(sampler, "settle-after-load").ConfigureAwait(false);

		Random random = new(options.Seed);
		string[] files = PickFiles(root, random);
		Dictionary<string, string> texts = [with(StringComparer.OrdinalIgnoreCase)];

		Begin(sampler, "open");
		log($"Opening {files.Length} files");

		foreach (string file in files) texts[file] = File.ReadAllText(file);

		await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, _) => {
			string text = texts[file];
			_latencies.Record("open (didOpen)", Measure(() => client.Documents.Open(file, text)));
			await _latencies.Time("projectContexts", () => client.Contexts.RequestAsync(file)).ConfigureAwait(false);
			await _latencies.Time("semanticTokens/full", () => client.SemanticTokens.FullAsync(file)).ConfigureAwait(false);
			await _latencies.Time("documentSymbol", () => client.Navigation.DocumentSymbolsAsync(file)).ConfigureAwait(false);
			await _latencies.Time("diagnostic pull", async () => {
				await client.Diagnostics.PullAsync(file).ConfigureAwait(false);
				return 0;
			}).ConfigureAwait(false);
		}).ConfigureAwait(false);

		await Settle(sampler, "settle-after-open").ConfigureAwait(false);

		if (!options.OpenOnly) await InteractAsync(client, sampler, files, texts, root).ConfigureAwait(false);

		if (!options.OpenOnly && files.Length > 0 && options.Keystrokes > 0) {
			await StopProfile().ConfigureAwait(false);
			Begin(sampler, "typing");
			string file = files[0];
			string original = texts[file];
			int insertAt = InsertionPoint(original, random);
			string typed = "var forgeWorkloadValue = System.Math.Max(1, 2);";
			log($"Typing {options.Keystrokes} keystrokes into {Path.GetFileName(file)}");

			for (int i = 0; i < options.Keystrokes; i++) {
				string current = original.Insert(insertAt, typed[..((i % typed.Length) + 1)]);
				_latencies.Record("didChange", Measure(() => client.Documents.Change(file, current)));
				(int line, int column) = LineColumn(current, insertAt + (i % typed.Length) + 1);
				await _latencies.Time("completion (typing)", () => client.Completion.RequestAsync(file, line, column, '\0', 1)).ConfigureAwait(false);
				await _latencies.Time("semanticTokens (typing)", () => client.SemanticTokens.FullAsync(file)).ConfigureAwait(false);
				await _latencies.Time("diagnostic pull (typing)", async () => {
					await client.Diagnostics.PullAsync(file).ConfigureAwait(false);
					return 0;
				}).ConfigureAwait(false);
			}

			client.Documents.Change(file, original);
			await Settle(sampler, "settle-after-typing").ConfigureAwait(false);
		}

		sampler.Phase = "close";

		foreach (string file in files) client.Documents.Close(file);

		await Settle(sampler, "idle").ConfigureAwait(false);
		await StopProfile().ConfigureAwait(false);
		_profiler?.Dispose();
		sampler.Phase = "done";

		IReadOnlyList<Sample> samples = sampler.Samples;
		client.Stop();

		List<string> removed = SourceGuard.RemoveCreated(intermediateBefore, SourceGuard.CaptureIntermediate(root));
		log($"Removed {removed.Count} obj files created during the run");

		List<string> changes = SourceGuard.Changes(before, SourceGuard.Capture(root));

		foreach (string change in changes) log($"  SOURCE CHANGED: {change}");

		return new WorkloadResult(root, workspace.Target, changes, options, loadSeconds, files.Length, diagnostics, Phases.Summarise(samples), _latencies.Summarise(), _heaps, _profiles, [.. samples]);
	}

	private async Task InteractAsync(RoslynLanguageClient client, Sampler sampler, string[] files, Dictionary<string, string> texts, string root) {
		await StopProfile().ConfigureAwait(false);
		Begin(sampler, "interact");
		log($"Hover, definition, references and completion in {files.Length} files");

		await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, _) => {
			string text = texts[file];
			Random positions = new(options.Seed ^ StableHash(Path.GetRelativePath(root, file)));

			foreach ((int line, int column) in PickPositions(text, positions)) {
				await _latencies.Time("hover", () => client.Hover.RequestAsync(file, line, column)).ConfigureAwait(false);
				await _latencies.Time("definition", () => client.Navigation.DefinitionsAsync(file, line, column)).ConfigureAwait(false);
				await _latencies.Time("completion", () => client.Completion.RequestAsync(file, line, column, '\0', 1)).ConfigureAwait(false);
			}

			if (PickPositions(text, positions).FirstOrDefault() is var (refLine, refColumn) && refLine > 0) await _latencies.Time("references", () => client.Navigation.ReferencesAsync(file, refLine, refColumn)).ConfigureAwait(false);
		}).ConfigureAwait(false);

		await Settle(sampler, "settle-after-interact").ConfigureAwait(false);
	}

	private void Begin(Sampler sampler, string phase) {
		sampler.Phase = phase;
		_profiler?.Start(phase);
	}

	private async Task StopProfile() {
		if (_profiler is not null && await _profiler.StopAsync(25).ConfigureAwait(false) is { } profile) _profiles.Add(profile);
	}

	private async Task Settle(Sampler sampler, string phase) {
		await StopProfile().ConfigureAwait(false);
		Begin(sampler, phase);
		log($"Settling {options.SettleSeconds}s ({phase})");
		await Task.Delay(TimeSpan.FromSeconds(options.SettleSeconds)).ConfigureAwait(false);

		await StopProfile().ConfigureAwait(false);

		if (!options.Census || phase is not ("settle-after-open" or "settle-after-interact" or "settle-after-typing" or "idle")) return;

		sampler.Phase = "census";
		List<HeapEntry> heap = HeapCensus.Take(20);
		_heaps[phase] = heap;
		log($"  heap after {phase}: {string.Join(", ", heap.Take(8).Select(static entry => $"{entry.Type} {entry.Bytes / 1048576.0:F1}MB x{entry.Count}"))}");
	}

	private static double Measure(Action action) {
		long start = System.Diagnostics.Stopwatch.GetTimestamp();
		action();
		return System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
	}

	private string[] PickFiles(string root, Random random) {
		string[] all = [.. Directory.EnumerateFiles(root, "*.cs", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
			.Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
			.Order(StringComparer.Ordinal)];

		Dictionary<string, string?> projectOfDirectory = [with(StringComparer.OrdinalIgnoreCase)];
		List<string> picked = [];

		foreach (IGrouping<string, string> project in all.GroupBy(path => ProjectFor(Path.GetDirectoryName(path)!, projectOfDirectory) ?? string.Empty, StringComparer.OrdinalIgnoreCase).Where(static group => group.Key.Length > 0).OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)) {
			string[] files = [.. project];
			new Random(options.Seed ^ StableHash(Path.GetRelativePath(root, project.Key))).Shuffle(files);
			picked.AddRange(files.Take(options.Files));
		}

		log($"Picked {picked.Count} files across {picked.Select(path => ProjectFor(Path.GetDirectoryName(path)!, projectOfDirectory)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} projects ({options.Files} per project)");
		return [.. picked];
	}

	private static string? ProjectFor(string directory, Dictionary<string, string?> cache) {
		if (cache.TryGetValue(directory, out string? cached)) return cached;

		string? project = Directory.EnumerateFiles(directory, "*.csproj").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
			?? (Path.GetDirectoryName(directory) is { } parent ? ProjectFor(parent, cache) : null);

		cache[directory] = project;
		return project;
	}

	private static int StableHash(string text) {
		uint hash = 2166136261;

		foreach (char c in text.ToLowerInvariant()) hash = (hash ^ c) * 16777619;

		return (int)hash;
	}

	private IEnumerable<(int Line, int Column)> PickPositions(string text, Random random) {
		MatchCollection matches = Identifier().Matches(text);

		if (matches.Count == 0) yield break;

		for (int i = 0; i < options.Positions; i++) {
			Match match = matches[random.Next(matches.Count)];
			yield return LineColumn(text, match.Index + match.Length / 2);
		}
	}

	private static int InsertionPoint(string text, Random random) {
		int[] candidates = [.. Regex.Matches(text, @"\{\r?\n").Select(static match => match.Index + match.Length)];
		return candidates.Length == 0 ? 0 : candidates[random.Next(candidates.Length)];
	}

	private static (int Line, int Column) LineColumn(string text, int offset) {
		int line = 0;
		int lineStart = 0;

		for (int i = 0; i < offset && i < text.Length; i++) {
			if (text[i] != '\n') continue;

			line++;
			lineStart = i + 1;
		}

		return (line, offset - lineStart);
	}

	private static string? DefaultServer() {
		for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
			if (!File.Exists(Path.Combine(directory.FullName, "Forge.slnx"))) continue;

			string bundled = Path.Combine(directory.FullName, "artifacts", "bin", "Forge.App", "Debug", "net11.0", "LanguageServer", "roslyn");
			return Directory.Exists(bundled) ? bundled : null;
		}

		return null;
	}
}
