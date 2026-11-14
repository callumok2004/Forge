using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.Dotnet.Build;
using Forge.Extensions.Dotnet.Server;

namespace Forge.Extensions.Dotnet.Language;

public sealed class DiagnosticsCoordinator
{
	private const int DebounceMilliseconds = 300;
	private const int PublishMilliseconds = 250;

	public const string Owner = "dotnet.roslyn";

	private readonly RoslynLanguageClient _server;
	private readonly Dictionary<string, IReadOnlyList<ServerDiagnostic>> _diagnostics = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, IReadOnlyList<ServerDiagnostic>> _workspace = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, long> _due = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	private IReadOnlyList<Diagnostic> _build = [];
	private IReadOnlySet<string> _buildOnly = new HashSet<string>();
	private ICommandHost? _host;
	private int _publishQueued;

	public DiagnosticsCoordinator(RoslynLanguageClient server) {
		_server = server;
		_server.Diagnostics.Published += OnPublished;
		_server.Diagnostics.WorkspacePublished += OnWorkspacePublished;
		_server.Diagnostics.WorkspaceReset += OnWorkspaceReset;
	}

	public void Attach(ICommandHost host) => _host = host;

	public void StartWorkspace() {
		_server.Diagnostics.StartWorkspace();

		_ = Task.Run(async () => {
			IReadOnlySet<string> ids = await _server.Diagnostics.BuildOnlyIdsAsync().ConfigureAwait(false);

			lock (_sync) _buildOnly = ids;

			Publish();
		});
	}

	public void StopWorkspace() => _server.Diagnostics.StopWorkspace();

	public void SetBuild(ICommandHost host, IReadOnlyList<Diagnostic> diagnostics) {
		_host ??= host;

		lock (_sync) _build = diagnostics;

		Publish();
	}

	public void Refresh(string path, bool immediate = false) {
		long due = Environment.TickCount64 + (immediate ? 0 : DebounceMilliseconds);

		lock (_sync) _due[path] = due;

		_ = Task.Run(async () => {
			if (!immediate) {
				await Task.Delay(DebounceMilliseconds).ConfigureAwait(false);

				lock (_sync) {
					if (_due.TryGetValue(path, out long latest) && latest != due) return;
				}
			}

			await Task.WhenAll(_server.Diagnostics.PullAsync(path), RefreshSemanticTokensAsync(path)).ConfigureAwait(false);
		});
	}

	public void RefreshOpen() {
		foreach (string path in _server.Documents.OpenPaths()) Refresh(path);
	}

	public void Clear(string path) {
		lock (_sync) {
			if (!_diagnostics.Remove(path)) return;
		}

		Publish();
	}

	private void OnPublished(string file, IReadOnlyList<ServerDiagnostic> diagnostics) {
		lock (_sync) _diagnostics[file] = diagnostics;

		Publish();
	}

	private void OnWorkspacePublished(string file, IReadOnlyList<ServerDiagnostic> diagnostics) {
		if (_server.Documents.IsOpen(file)) return;

		lock (_sync) _workspace[file] = diagnostics;

		Schedule();
	}

	private void OnWorkspaceReset() {
		lock (_sync) _workspace.Clear();

		Publish();
	}

	private void Schedule() {
		if (Interlocked.Exchange(ref _publishQueued, 1) == 1) return;

		_ = Task.Run(async () => {
			await Task.Delay(PublishMilliseconds).ConfigureAwait(false);
			Publish();
		});
	}

	private void Publish() {
		Interlocked.Exchange(ref _publishQueued, 0);

		if (_host is not { } host) return;

		Diagnostic[] live;
		Diagnostic[] build;

		lock (_sync) {
			live = Live();
			build = _build.Count == 0 ? [] : [.. _build.Select(diagnostic => Keep(diagnostic) ? diagnostic : diagnostic with { Superseded = true })];
		}

		host.ReportProblems(Owner, false, live);
		host.ReportProblems(MsBuildProblemMatcher.Owner, true, build);
		host.Refresh();
	}

	private Diagnostic[] Live() => Merge(_diagnostics, _workspace);

	internal static Diagnostic[] Merge(IReadOnlyDictionary<string, IReadOnlyList<ServerDiagnostic>> documents, IReadOnlyDictionary<string, IReadOnlyList<ServerDiagnostic>> workspace) {
		List<Diagnostic> all = [];

		foreach (IReadOnlyList<ServerDiagnostic> file in documents.Values) Add(all, file);

		foreach ((string path, IReadOnlyList<ServerDiagnostic> file) in workspace) {
			if (!documents.ContainsKey(path)) Add(all, file);
		}

		return [.. all];
	}

	private static void Add(List<Diagnostic> all, IReadOnlyList<ServerDiagnostic> file) {
		foreach (ServerDiagnostic diagnostic in file) all.Add(new Diagnostic(diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Message, diagnostic.Severity, diagnostic.Code, diagnostic.EndLine, diagnostic.EndColumn, diagnostic.Href));
	}

	private bool Keep(Diagnostic diagnostic) => KeepBuild(diagnostic, _buildOnly, _diagnostics.ContainsKey, _workspace.ContainsKey, FullSolution("dotnet.backgroundAnalysis.compilerDiagnosticsScope", "fullSolution"), FullSolution("dotnet.backgroundAnalysis.analyzerDiagnosticsScope", "openFiles"));

	internal static bool KeepBuild(Diagnostic diagnostic, IReadOnlySet<string> buildOnly, Func<string, bool> documentPulled, Func<string, bool> workspaceReported, bool compilerFullSolution, bool analyzerFullSolution) {
		if (diagnostic.Code is { } code && buildOnly.Contains(code)) return true;

		if (documentPulled(diagnostic.File)) return false;

		if (!workspaceReported(diagnostic.File)) return true;

		return !(IsCompiler(diagnostic.Code) ? compilerFullSolution : analyzerFullSolution);
	}

	private bool FullSolution(string setting, string fallback) => string.Equals(_server.Settings?.GetString(setting) ?? fallback, "fullSolution", StringComparison.OrdinalIgnoreCase);

	private static bool IsCompiler(string? code) => code is { Length: > 2 } && code.StartsWith("CS", StringComparison.Ordinal) && char.IsAsciiDigit(code[2]);

	private async Task RefreshSemanticTokensAsync(string path) {
		if (_host is not { } host) return;

		IReadOnlyList<ServerToken> tokens = await _server.SemanticTokens.FullAsync(path).ConfigureAwait(false);

		if (tokens.Count == 0) return;

		List<SemanticToken> coloured = [with(tokens.Count)];

		foreach (ServerToken token in tokens) {
			if (Syntax.SemanticColors.For(token.Type) is not { } colour) continue;

			coloured.Add(new SemanticToken(token.Line, token.StartColumn, token.Length, ((uint)(colour.A * 255f) << 24) | ((uint)(colour.R * 255f) << 16) | ((uint)(colour.G * 255f) << 8) | (uint)(colour.B * 255f)));
		}

		host.SetSemanticTokens(path, coloured);
		host.Refresh();
	}
}
