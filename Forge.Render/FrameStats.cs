using System.Diagnostics;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class FrameStats : IFrameStats
{
	private const int HistorySize = 60;

	private readonly double[] _history = new double[HistorySize];
	private readonly double[] _damageHistory = new double[HistorySize];
	private readonly Stopwatch _stopwatch = new();

	private int _index;
	private int _count;

	private double _lastFrameMs;
	private double _averageFrameMs;
	private double _peakFrameMs;
	private long _frameCount;

	private double _lastDamagePercent;
	private double _averageDamagePercent;
	private double _peakDamagePercent;

	public double GetLastFrameMs() => _lastFrameMs;

	public double GetAverageFrameMs() => _averageFrameMs;

	public double GetPeakFrameMs() => _peakFrameMs;

	public long GetFrameCount() => _frameCount;

	public double GetImpliedFps() => _averageFrameMs > 0.0001 ? 1000.0 / _averageFrameMs : 0.0;

	public double GetLastDamagePercent() => _lastDamagePercent;

	public double GetAverageDamagePercent() => _averageDamagePercent;

	public double GetPeakDamagePercent() => _peakDamagePercent;

	public void BeginFrame(double damageFraction = 1.0) {
		_lastDamagePercent = Math.Clamp(damageFraction, 0.0, 1.0) * 100.0;
		_damageHistory[_index] = _lastDamagePercent;
		_stopwatch.Restart();
	}

	public void EndFrame() {
		_stopwatch.Stop();

		_lastFrameMs = _stopwatch.Elapsed.TotalMilliseconds;
		_frameCount++;

		_history[_index] = _lastFrameMs;
		_index = (_index + 1) % HistorySize;
		_count = Math.Min(_count + 1, HistorySize);

		double total = 0.0;
		double damage = 0.0;
		double peak = 0.0;
		double damagePeak = 0.0;

		for (int i = 0; i < _count; i++) {
			total += _history[i];
			damage += _damageHistory[i];

			if (_history[i] > peak) peak = _history[i];

			if (_damageHistory[i] > damagePeak) damagePeak = _damageHistory[i];
		}

		_averageFrameMs = total / _count;
		_averageDamagePercent = damage / _count;
		_peakFrameMs = peak;
		_peakDamagePercent = damagePeak;
	}
}
