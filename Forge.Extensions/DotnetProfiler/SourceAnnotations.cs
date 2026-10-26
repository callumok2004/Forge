using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Extensions.DotnetProfiler.Views;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler;

public sealed class SourceAnnotations(ICommandHost host)
{
	public const string Owner = "dotnetProfiler";
	public const int MaximumRows = 80;

	private static readonly Color Hot = Color.Parse("#e06c75e6");
	private static readonly Color Warm = Color.Parse("#dcb45ae6");
	private static readonly Color Cool = Color.Parse("#7f7f7fbf");

	private int _generation;

	public static (string? Type, string Method) SplitName(string fullName) {
		ReadOnlySpan<char> bare = FrameNames.WithoutArguments(fullName);
		List<string> parts = [];

		foreach (Range range in bare.SplitAny(".+")) {
			if (bare[range].Length > 0) parts.Add(bare[range].ToString());
		}

		if (parts.Count == 0) return (null, bare.ToString());

		string method = parts[^1];
		parts.RemoveAt(parts.Count - 1);
		string? type = parts.Count > 0 ? parts[^1] : null;

		return method is "ctor" or "cctor" && type is not null ? (type, type) : (type, method);
	}

	public async Task<WorkspaceSymbol?> FindAsync(string fullName) {
		if (host.GetLanguageFeatures() is not { } features) return null;

		(string? type, string method) = SplitName(fullName);
		IReadOnlyList<WorkspaceSymbol> symbols;

		try {
			symbols = await features.WorkspaceSymbolsAsync(method).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException) {
			return null;
		}

		WorkspaceSymbol? best = null;
		int bestScore = -1;

		foreach (WorkspaceSymbol symbol in symbols) {
			if (symbol.Name != method && !symbol.Name.StartsWith(method + "(", StringComparison.Ordinal)) continue;

			int score = symbol.Kind is SymbolKind.Method or SymbolKind.Function or SymbolKind.Constructor or SymbolKind.Property ? 2 : 0;

			if (type is not null && symbol.Container is { } container && container.EndsWith(type, StringComparison.Ordinal)) score++;

			if (score <= bestScore) continue;

			best = symbol;
			bestScore = score;
		}

		return best;
	}

	public async Task OpenAsync(string fullName) {
		if (await FindAsync(fullName).ConfigureAwait(false) is { } symbol) {
			host.GoTo(symbol.Path, symbol.Line, symbol.Column);
			return;
		}

		host.ShowMessage(MessageSeverity.Info, $"Could not find {SplitName(fullName).Method} in the workspace.", []);
	}

	public void Clear() {
		_generation++;
		host.SetLineHints(Owner, null);
	}

	public async Task SetAsync(ProfileViewState state) {
		int generation = ++_generation;

		if (state.Cpu is not { } cpu) {
			host.SetLineHints(Owner, null);
			return;
		}

		double root = Math.Max(1, cpu.RootTotal);
		List<FunctionRow> rows = [.. cpu.Rows.Where(static row => !row.System && row.Total >= ProfileViewState.LowConfidence).OrderByDescending(static row => row.Total).Take(MaximumRows)];
		Dictionary<string, Dictionary<int, LineHint>> byPath = new(StringComparer.OrdinalIgnoreCase);

		foreach (FunctionRow row in rows) {
			WorkspaceSymbol? symbol = await FindAsync(row.Name).ConfigureAwait(false);

			if (generation != _generation) return;

			if (symbol is not { } found) continue;

			long alloc = state.AllocOwn(row);
			string text = $"⏱ own {state.Format(Metric.Cpu, row.Own)} ({state.Percent(Metric.Cpu, row.Own)}) · total {state.Format(Metric.Cpu, row.Total)}" + (alloc > 0 ? $" · alloc {state.FormatValue(Metric.Alloc, state.Convert(Metric.Alloc, alloc))}" : string.Empty);
			double heat = row.Own / root;
			Color color = heat >= 0.05 ? Hot : heat >= 0.01 ? Warm : Cool;

			if (!byPath.TryGetValue(found.Path, out Dictionary<int, LineHint>? hints)) byPath[found.Path] = hints = [];

			hints.TryAdd(found.Line - 1, new LineHint(text, false, color));
		}

		host.SetLineHints(Owner, byPath.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyDictionary<int, LineHint>)pair.Value, StringComparer.OrdinalIgnoreCase));
	}
}
