using System.Diagnostics.Tracing;

using Microsoft.Diagnostics.NETCore.Client;

namespace Forge.Extensions.Dotnet.Server;

public static class ServerHeapTrim
{
	private const long GCHeapCollectKeyword = 0x800000;
	private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)];

	private static int _scheduled;

	public static void Schedule(int processId, Action<string>? log) {
		if (Interlocked.Exchange(ref _scheduled, processId) == processId) return;

		_ = Task.Run(async () => {
			foreach (TimeSpan delay in Delays) {
				await Task.Delay(delay).ConfigureAwait(false);
				await CollectAsync(processId, log).ConfigureAwait(false);
			}
		});
	}

	private static async Task CollectAsync(int processId, Action<string>? log) {
		if (!DiagnosticsClient.GetPublishedProcesses().Contains(processId)) return;

		try {
			DiagnosticsClient client = new(processId);
			EventPipeProvider provider = new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, GCHeapCollectKeyword);

			using EventPipeSession session = client.StartEventPipeSession([provider], requestRundown: false, circularBufferMB: 4);
			Task drain = session.EventStream.CopyToAsync(Stream.Null);

			await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
			session.Stop();
			await drain.ConfigureAwait(false);
			log?.Invoke("Compacted the language server heap after project load.");
		}
		catch (Exception exception) {
			log?.Invoke($"Could not compact the language server heap: {exception.Message}");
		}
	}
}
