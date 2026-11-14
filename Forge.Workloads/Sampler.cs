using System.Diagnostics;

using Forge.Core.Native;

namespace Forge.Workloads;

internal readonly record struct Sample(double Seconds, string Phase, double ForgeCpu, long ForgeMemory, long ForgeManaged, double RoslynCpu, long RoslynMemory, int RoslynProcesses);

internal sealed class Sampler : IDisposable
{
	private readonly ProcessTree _tree = new();
	private readonly List<Sample> _samples = [];
	private readonly Lock _sync = new();
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private readonly CancellationTokenSource _stop = new();
	private readonly Thread _thread;
	private readonly int _intervalMilliseconds;
	private readonly uint _self = (uint)Environment.ProcessId;

	private volatile string _phase = "start";

	public Sampler(int intervalMilliseconds) {
		_intervalMilliseconds = intervalMilliseconds;
		_tree.Sample();
		_thread = new Thread(Run) { IsBackground = true, Name = "Workload sampler", Priority = ThreadPriority.AboveNormal };
		_thread.Start();
	}

	public string Phase {
		get => _phase;
		set => _phase = value;
	}

	public double Elapsed => _clock.Elapsed.TotalSeconds;

	public IReadOnlyList<Sample> Samples {
		get {
			lock (_sync) return [.. _samples];
		}
	}

	private void Run() {
		while (!_stop.IsCancellationRequested) {
			if (_stop.Token.WaitHandle.WaitOne(_intervalMilliseconds)) break;

			_tree.Sample();
			double forgeCpu = 0;
			long forgeMemory = 0;
			double roslynCpu = 0;
			long roslynMemory = 0;
			int roslynProcesses = 0;
			double cores = Environment.ProcessorCount / 100.0;

			foreach (ProcessUsage row in _tree.Rows) {
				if (row.Id == _self) {
					forgeCpu = row.CpuPercent * cores;
					forgeMemory = row.MemoryBytes;
					continue;
				}

				roslynCpu += row.CpuPercent * cores;
				roslynMemory += row.MemoryBytes;
				roslynProcesses++;
			}

			Sample sample = new(_clock.Elapsed.TotalSeconds, _phase, forgeCpu, forgeMemory, GC.GetTotalMemory(false), roslynCpu, roslynMemory, roslynProcesses);

			lock (_sync) _samples.Add(sample);
		}
	}

	public void Dispose() {
		_stop.Cancel();
		_thread.Join();
		_stop.Dispose();
	}
}
