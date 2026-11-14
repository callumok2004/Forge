using System.Diagnostics.Tracing;

using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Forge.Workloads;

internal sealed record HotMethod(string Name, double SelfPercent, double InclusivePercent);

internal sealed record ServerProfile(string Phase, long Samples, double DurationMs, List<HotMethod> BySelf, List<HotMethod> ByInclusive, Dictionary<string, List<string>> Callers);

internal sealed class ServerProfiler(int processId, Action<string> log) : IDisposable
{
	private const long RuntimeKeywords = 0x14C14FCCBD;

	private readonly DiagnosticsClient _client = new(processId);
	private EventPipeSession? _session;
	private Task? _copy;
	private string? _file;
	private string? _phase;
	private long _started;

	public void Start(string phase) {
		_phase = phase;
		_file = Path.Combine(Path.GetTempPath(), "forge-workloads", $"roslyn-{phase}-{Environment.ProcessId}.nettrace");
		Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
		_started = System.Diagnostics.Stopwatch.GetTimestamp();

		_session = _client.StartEventPipeSession([
			new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
			new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, RuntimeKeywords),
		], requestRundown: true, circularBufferMB: 512);

		FileStream output = File.Create(_file);
		EventPipeSession session = _session;
		_copy = Task.Run(async () => {
			await using (output) await session.EventStream.CopyToAsync(output).ConfigureAwait(false);
		});
	}

	public async Task<ServerProfile?> StopAsync(int top) {
		if (_phase is not { } phase || _session is not { } session || _file is not { } file) return null;

		double duration = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
		_phase = null;
		_session = null;

		try {
			await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
			await _copy!.ConfigureAwait(false);
		}
		finally {
			session.Dispose();
		}

		ServerProfile? result = await Task.Run(() => Analyse(phase, file, duration, top)).ConfigureAwait(false);
		TryDelete(file);
		TryDelete(Path.ChangeExtension(file, ".etlx"));
		log($"  roslyn profile ({phase}): {result?.Samples ?? 0} samples");
		return result;
	}

	private static ServerProfile? Analyse(string phase, string file, double duration, int top) {
		string etlx = TraceLog.CreateFromEventPipeDataFile(file, Path.ChangeExtension(file, ".etlx"));
		using TraceLog trace = new(etlx);
		Dictionary<string, long> self = [with(StringComparer.Ordinal)];
		Dictionary<string, long> inclusive = [with(StringComparer.Ordinal)];
		Dictionary<string, Dictionary<string, long>> paths = [with(StringComparer.Ordinal)];
		HashSet<string> seen = [with(StringComparer.Ordinal)];
		List<string> frames = [];
		long samples = 0;

		foreach (TraceEvent data in trace.Events) {
			if (data is not ClrThreadSampleTraceData sample || sample.Type != ClrThreadSampleType.Managed) continue;

			TraceCallStack? stack = data.CallStack();

			if (stack is null) continue;

			samples++;
			frames.Clear();

			for (TraceCallStack? frame = stack; frame is not null; frame = frame.Caller) {
				string name = frame.CodeAddress.FullMethodName;
				frames.Add(string.IsNullOrEmpty(name) ? $"0x{frame.CodeAddress.Address:x} ({frame.CodeAddress.ModuleName})" : name);
			}

			self[frames[0]] = self.GetValueOrDefault(frames[0]) + 1;
			seen.Clear();

			foreach (string name in frames)
				if (seen.Add(name)) inclusive[name] = inclusive.GetValueOrDefault(name) + 1;

			if (!paths.TryGetValue(frames[0], out Dictionary<string, long>? callers)) paths[frames[0]] = callers = [with(StringComparer.Ordinal)];

			string key = string.Join(" <- ", frames.Skip(1).Where(static name => !name.StartsWith("0x", StringComparison.Ordinal)).SkipWhile(static name => name.StartsWith("System.", StringComparison.Ordinal)).Take(6).Select(Short));
			callers[key] = callers.GetValueOrDefault(key) + 1;
		}

		if (samples == 0) return new ServerProfile(phase, 0, duration, [], [], []);

		double total = samples;

		List<HotMethod> Rank(Dictionary<string, long> weights) => [.. weights.Where(static pair => Interesting(pair.Key)).OrderByDescending(static pair => pair.Value).Take(top)
			.Select(pair => new HotMethod(pair.Key, self.GetValueOrDefault(pair.Key) * 100 / total, inclusive.GetValueOrDefault(pair.Key) * 100 / total))];

		List<HotMethod> bySelf = Rank(self);
		Dictionary<string, List<string>> topCallers = bySelf.Take(8).ToDictionary(static method => method.Name, method => paths[method.Name].OrderByDescending(static entry => entry.Value).Take(4).Select(entry => $"{entry.Value * 100 / total:F1}%  {entry.Key}").ToList());

		return new ServerProfile(phase, samples, duration, bySelf, Rank(inclusive), topCallers);
	}

	private static void TryDelete(string path) {
		try {
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private static string Short(string name) {
		int paren = name.IndexOf('(');
		string head = paren > 0 ? name[..paren] : name;
		int dot = head.LastIndexOf('.', Math.Max(0, head.LastIndexOf('.') - 1));
		return dot > 0 ? head[(dot + 1)..] : head;
	}

	private static bool Interesting(string name) => !name.Contains("Thread.StartCallback", StringComparison.Ordinal) && !name.StartsWith("System.Threading.ThreadPoolWorkQueue", StringComparison.Ordinal) && !name.StartsWith("System.Threading.PortableThreadPool", StringComparison.Ordinal);

	public void Dispose() => _session?.Dispose();
}
