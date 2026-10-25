using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.Dotnet.Server;

namespace Forge.Extensions.Dotnet.Language;

public sealed class DiagnosticsCoordinator
{
	private const int DebounceMilliseconds = 300;

	private readonly RoslynLanguageClient _server;
	private readonly Dictionary<string, IReadOnlyList<ServerDiagnostic>> _diagnostics = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, long> _due = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	private ICommandHost? _host;

	public DiagnosticsCoordinator(RoslynLanguageClient server) {
		_server = server;
		_server.Diagnostics.Published += OnPublished;
	}

	public void Attach(ICommandHost host) => _host = host;

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

			await _server.Diagnostics.PullAsync(path).ConfigureAwait(false);
			await RefreshSemanticTokensAsync(path).ConfigureAwait(false);
		});
	}

	private void OnPublished(string file, IReadOnlyList<ServerDiagnostic> diagnostics) {
		lock (_sync) {
			if (diagnostics.Count == 0) _diagnostics.Remove(file);
			else _diagnostics[file] = diagnostics;
		}

		if (_host is not { } host) return;

		host.ReportProblems(All());
		host.Refresh();
	}

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

	private Diagnostic[] All() {
		lock (_sync) {
			return [.. _diagnostics.Values.SelectMany(static file => file).OrderByDescending(static diagnostic => diagnostic.IsError).Select(static diagnostic => new Diagnostic(diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Message, diagnostic.IsError))];
		}
	}
}
