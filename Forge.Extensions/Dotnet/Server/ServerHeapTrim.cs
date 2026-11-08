using System.Diagnostics;
using System.Diagnostics.Tracing;

using Microsoft.Diagnostics.NETCore.Client;

namespace Forge.Extensions.Dotnet.Server;

public static class ServerHeapTrim
{
	private const long GCHeapCollectKeyword = 0x800000;
	private const long Threshold = 1L << 30;
	private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

	private static int _watching;
	private static Timer? _timer;
	private static int _collecting;

	public static void Schedule(int processId) {
		if (Interlocked.Exchange(ref _watching, processId) == processId) return;

		_timer?.Dispose();
		_timer = new Timer(_ => Check(processId), null, Interval, Interval);
	}

	private static void Check(int processId) {
		if (Volatile.Read(ref _watching) != processId) return;

		long memory;

		try {
			using Process process = Process.GetProcessById(processId);
			memory = process.PrivateMemorySize64;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) {
			if (Interlocked.CompareExchange(ref _watching, 0, processId) == processId) {
				_timer?.Dispose();
				_timer = null;
			}

			return;
		}

		if (memory < Threshold || Interlocked.Exchange(ref _collecting, 1) == 1) return;

		_ = Task.Run(async () => {
			try {
				await CollectAsync(processId).ConfigureAwait(false);
			}
			finally {
				Volatile.Write(ref _collecting, 0);
			}
		});
	}

	private static async Task CollectAsync(int processId) {
		try {
			if (!DiagnosticsClient.GetPublishedProcesses().Contains(processId)) return;

			DiagnosticsClient client = new(processId);
			EventPipeProvider provider = new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, GCHeapCollectKeyword);

			using EventPipeSession session = client.StartEventPipeSession([provider], requestRundown: false, circularBufferMB: 4);
			Task drain = session.EventStream.CopyToAsync(Stream.Null);

			await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
			session.Stop();
			await drain.ConfigureAwait(false);
		}
		catch (Exception) {
		}
	}
}
