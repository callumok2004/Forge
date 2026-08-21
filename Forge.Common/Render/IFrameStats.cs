namespace Forge.Common.Render;

public interface IFrameStats
{
	double GetLastFrameMs();
	double GetAverageFrameMs();
	double GetPeakFrameMs();

	double GetLastDamagePercent();
	double GetAverageDamagePercent();
	double GetPeakDamagePercent();

	long GetFrameCount();
	double GetImpliedFps();

	void BeginFrame(double damageFraction = 1.0);
	void EndFrame();
}
