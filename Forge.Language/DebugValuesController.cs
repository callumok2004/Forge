using Forge.Common.Commands;
using Forge.Common.Debug;
using Forge.Common.Editor;
using Forge.Editor;
using Forge.Text;

namespace Forge.Language;

public sealed class DebugValuesController(IEditorHost host, UiQueue ui)
{
	public const int InlineDelayMilliseconds = 200;
	public const int MaximumInlineValues = 100;
	public const int MaximumLineLength = 500;
	public const string ValueFormat = "{0} = {1}";
	public const string ValueSeparator = ", ";

	private readonly Dictionary<string, Task<string?>> _evaluated = [];
	private readonly List<InlineValueLookup> _lookups = [];

	private Dictionary<string, string>? _scopeValues;
	private int _cachedFrame = -1;
	private (int Frame, string Path, int Version, int First, int Last) _inlineKey = (-1, string.Empty, -1, -1, -1);
	private long _inlineDue;
	private int _generation;
	private TextView? _decorated;

	public DebugHoverWidget Hover { get; } = new();

	public static (int Start, int End) GenericExpression(ReadOnlySpan<char> line, int column) {
		int start = column;
		int end = column;

		while (start > 0 && IsExpressionCharacter(line, start - 1)) start--;

		while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_')) end++;

		return (start, end);
	}

	private static bool IsExpressionCharacter(ReadOnlySpan<char> line, int index) => line[index] switch {
		'(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or '+' or '-' or '/' or '%' or '~' or '#' or '^' or ';' or '=' or '|' or ',' or '`' or '!' => false,
		_ => !char.IsWhiteSpace(line[index]),
	};

	private void ResetFrameCache(int frame) {
		if (frame == _cachedFrame) return;

		_cachedFrame = frame;
		_scopeValues = null;
		_evaluated.Clear();
	}

	public bool TryHover(IDebug debug, ILanguageFeatures? features, string path, ITextView view, int line, int column, float anchorX, float lineTop, float lineHeight, Func<int, float> columnX, Action unavailable) {
		if (debug.GetState() != DebugState.Stopped || debug.GetFocusedFrame() is not { } frame) return false;

		TextSnapshot snapshot = view.GetSnapshot();

		if (line < 1 || line > snapshot.LineCount) return false;

		TextSpan span = snapshot.GetLineSpan(line - 1);
		string text = snapshot.GetText(span.Start, span.Length);
		int at = Math.Clamp(column - 1, 0, text.Length);
		EvaluatableExpression? found = features?.EvaluatableExpressionAt(path, line, text, at);

		if (found is null) {
			if (features is not null && features.Handles(path)) return false;

			(int start, int end) = GenericExpression(text, at);

			if (end <= start) return false;

			found = new EvaluatableExpression(start, end, text[start..end]);
		}

		EvaluatableExpression expression = found.Value;

		if (Hover.IsOpen && Hover.Line == line && Hover.StartColumn == expression.StartColumn && Hover.EndColumn == expression.EndColumn) return true;

		ResetFrameCache(frame.Id);

		int generation = ++_generation;
		bool forHovers = debug.GetCapabilities().EvaluateForHovers;

		_ = Task.Run(async () => {
			DebugVariable? result = forHovers ? await EvaluateAsync(debug, expression.Expression).ConfigureAwait(false) ?? await FromScopesAsync(debug, frame.Id, expression.Expression).ConfigureAwait(false) : await FromScopesAsync(debug, frame.Id, expression.Expression).ConfigureAwait(false) ?? await EvaluateAsync(debug, expression.Expression).ConfigureAwait(false);

			ui.Post(() => {
				if (generation != _generation) return;

				if (result is null) {
					Hover.Hide();
					unavailable();
					return;
				}

				Hover.LoadChildren = debug.VariablesAsync;
				Hover.Show(result, line, expression.StartColumn, expression.EndColumn, columnX(expression.StartColumn), lineTop, lineHeight);
			});
		});

		return true;
	}

	public void CancelHover() {
		_generation++;
		Hover.Hide();
	}

	private static async Task<DebugVariable?> EvaluateAsync(IDebug debug, string expression) {
		DebugVariable? result = await debug.EvaluateAsync(expression, "hover").ConfigureAwait(false);

		return result is { Value.Length: > 0 } ? result : null;
	}

	private static async Task<DebugVariable?> FromScopesAsync(IDebug debug, int frame, string expression) {
		string[] names = [.. expression.Split('.').Select(static name => name.Trim()).Where(static name => name.Length > 0)];

		if (names.Length == 0) return null;

		List<DebugVariable> found = [];

		foreach (DebugScope scope in await debug.ScopesAsync(frame).ConfigureAwait(false)) {
			if (scope.Expensive) continue;

			if (await FindAsync(debug, scope.VariablesReference, names, 0).ConfigureAwait(false) is { } match) found.Add(match);
		}

		return found.Count > 0 && found.TrueForAll(match => match.Value == found[0].Value) ? found[0] : null;
	}

	private static async Task<DebugVariable?> FindAsync(IDebug debug, int reference, string[] names, int index) {
		IReadOnlyList<DebugVariable> children = await debug.VariablesAsync(reference).ConfigureAwait(false);
		DebugVariable? match = null;
		int matches = 0;

		foreach (DebugVariable child in children) {
			if (child.Name != names[index]) continue;

			match = child;
			matches++;
		}

		if (matches != 1 || match is null) return null;

		return index == names.Length - 1 ? match : match.VariablesReference > 0 ? await FindAsync(debug, match.VariablesReference, names, index + 1).ConfigureAwait(false) : null;
	}

	public void Tick(IDebug? debug, ILanguageFeatures? features, bool enabled, float editorWidth, float cellWidth) {
		if (host.GetActiveView() is not TextView view || host.GetActiveFilePath() is not { } path) {
			Clear();
			return;
		}

		if (debug is null || !enabled || features is null || !features.ProvidesInlineValues || debug.GetState() != DebugState.Stopped || debug.GetFocusedFrame() is not { SourcePath: { } source } frame || !string.Equals(source, path, StringComparison.OrdinalIgnoreCase)) {
			Clear();
			return;
		}

		float lineHeight = view.LastLineHeight;
		float viewport = view.LastViewportHeight;
		int visibleFirst = view.LineAtY(view.ScrollY, lineHeight);
		int visibleLast = view.LineAtY(view.ScrollY + viewport, lineHeight);
		int span = visibleLast - visibleFirst + 1;
		int first = Math.Max(0, visibleFirst - span);
		int last = Math.Min(view.Snapshot.LineCount - 1, Math.Min(frame.Line - 1, visibleLast + span));
		var key = (frame.Id, path, view.Buffer.Version, first, last);

		if (key != _inlineKey) {
			_inlineKey = key;
			_inlineDue = Environment.TickCount64 + InlineDelayMilliseconds;
		}

		if (_inlineDue == 0 || Environment.TickCount64 < _inlineDue) return;

		_inlineDue = 0;
		ResetFrameCache(frame.Id);

		_lookups.Clear();

		if (last >= first) {
			TextSnapshot snapshot = view.Snapshot;
			features.InlineValues(path, first, last, line => snapshot.GetLineLength(line) > MaximumLineLength ? string.Empty : snapshot.GetText(snapshot.GetLineSpan(line)), _lookups);
		}

		InlineValueLookup[] lookups = [.. _lookups];
		int generation = _generation;
		int maximumColumns = (int)MathF.Floor((editorWidth - 50f) / MathF.Max(1f, cellWidth));

		_ = Task.Run(async () => {
			Dictionary<int, List<(int Column, string Text)>> lines = [];
			int count = 0;

			foreach (InlineValueLookup lookup in lookups) {
				if (count >= MaximumInlineValues) break;

				if (await FindVariableAsync(debug, frame.Id, lookup.Name).ConfigureAwait(false) is not { Length: > 0 } value) continue;

				string text = string.Format(System.Globalization.CultureInfo.InvariantCulture, ValueFormat, lookup.Name, value);

				if (!lines.TryGetValue(lookup.Line, out List<(int Column, string Text)>? segments)) lines[lookup.Line] = segments = [];

				if (segments.Exists(segment => segment.Text == text)) continue;

				segments.Add((lookup.StartColumn, text));
				count++;
			}

			Dictionary<int, string> rendered = [];

			foreach ((int line, List<(int Column, string Text)> segments) in lines) {
				segments.Sort(static (left, right) => left.Column.CompareTo(right.Column));
				string text = string.Join(ValueSeparator, segments.Select(static segment => segment.Text));

				if (maximumColumns > 0 && text.Length > maximumColumns) text = string.Concat(text.AsSpan(0, maximumColumns), "...");

				rendered[line] = text;
			}

			ui.Post(() => {
				if (_inlineKey.Frame != frame.Id) return;

				view.InlineValues = rendered;
				_decorated = view;
			});
		});
	}

	private async Task<string?> FindVariableAsync(IDebug debug, int frame, string name) {
		Dictionary<string, string> values = _scopeValues ??= await ScopeValuesAsync(debug, frame).ConfigureAwait(false);

		if (values.TryGetValue(name, out string? value)) return value;

		Task<string?> pending;

		lock (_evaluated) {
			if (!_evaluated.TryGetValue(name, out pending!)) _evaluated[name] = pending = EvaluateValueAsync(debug, name);
		}

		return await pending.ConfigureAwait(false);
	}

	private static async Task<string?> EvaluateValueAsync(IDebug debug, string name) => (await debug.EvaluateAsync(name, "hover").ConfigureAwait(false))?.Value is { Length: > 0 } value ? value : null;

	private static async Task<Dictionary<string, string>> ScopeValuesAsync(IDebug debug, int frame) {
		Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

		foreach (DebugScope scope in await debug.ScopesAsync(frame).ConfigureAwait(false)) {
			if (scope.Expensive) continue;

			foreach (DebugVariable variable in await debug.VariablesAsync(scope.VariablesReference).ConfigureAwait(false)) {
				values.TryAdd(variable.Name, variable.Value);

				if (values.Count >= MaximumInlineValues) return values;
			}
		}

		return values;
	}

	private void Clear() {
		_inlineKey = (-1, string.Empty, -1, -1, -1);
		_inlineDue = 0;

		if (_decorated is null) return;

		_decorated.InlineValues = null;
		_decorated = null;
	}
}
