using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Horology;

namespace Forge.Benchmarks;

internal static class ForgeConfig
{
	public static IConfig Create() =>
		ManualConfig.Create(DefaultConfig.Instance)
			.AddJob(Job.ShortRun
				.WithWarmupCount(2)
				.WithIterationCount(4)
				.WithIterationTime(TimeInterval.FromMilliseconds(100))
				.WithToolchain(InProcessEmitToolchain.Instance)
				.WithId("Quick"))
			.WithOptions(ConfigOptions.DisableLogFile);
}
