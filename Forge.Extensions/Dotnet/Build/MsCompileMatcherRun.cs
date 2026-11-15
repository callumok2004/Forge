using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Language;

namespace Forge.Extensions.Dotnet.Build;

public sealed class MsCompileMatcherRun : IProblemMatcherRun
{
	public const string Name = "$msCompile";

	private readonly MsBuildProblemMatcher _matcher = new();
	private readonly DiagnosticsCoordinator _diagnostics;
	private readonly ICommandHost _host;
	private readonly Lock _sync = new();

	public MsCompileMatcherRun(DiagnosticsCoordinator diagnostics, ICommandHost host) {
		_diagnostics = diagnostics;
		_host = host;
		_diagnostics.SetBuild(host, []);
	}

	public void Read(string line) {
		Diagnostic[] snapshot;

		lock (_sync) {
			if (!_matcher.Read(line)) return;

			snapshot = [.. _matcher.Diagnostics];
		}

		_diagnostics.SetBuild(_host, snapshot, final: false);
	}

	public void Finished(int exitCode) {
		Diagnostic[] snapshot;

		lock (_sync) snapshot = [.. _matcher.Diagnostics];

		_diagnostics.SetBuild(_host, snapshot);
	}
}
