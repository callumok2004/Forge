using BenchmarkDotNet.Running;

namespace Forge.Benchmarks;

public static class Program
{
	public static void Main(string[] args) {
		if (args is ["startup"]) {
			GrammarStartup.Run();
			return;
		}

		BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, ForgeConfig.Create());
	}
}
