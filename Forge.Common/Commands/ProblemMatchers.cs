namespace Forge.Common.Commands;

public interface IProblemMatcherRun
{
	void Read(string line);

	void Finished(int exitCode);
}

public static class ProblemMatchers
{
	private static readonly Dictionary<string, Func<ICommandHost, IProblemMatcherRun>> Factories = new(StringComparer.Ordinal);
	private static readonly Lock Sync = new();

	public static void Register(string name, Func<ICommandHost, IProblemMatcherRun> factory) {
		lock (Sync) Factories[name] = factory;
	}

	public static (Action<string>? Lines, Action<int>? Finished) Start(ICommandHost host, IReadOnlyList<string> names) {
		List<IProblemMatcherRun> runs = [];

		lock (Sync) {
			foreach (string name in names) {
				if (Factories.TryGetValue(name, out Func<ICommandHost, IProblemMatcherRun>? factory)) runs.Add(factory(host));
			}
		}

		if (runs.Count == 0) return (null, null);

		return (line => {
			foreach (IProblemMatcherRun run in runs) run.Read(line);
		}, exitCode => {
			foreach (IProblemMatcherRun run in runs) run.Finished(exitCode);
		});
	}
}
