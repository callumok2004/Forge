using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Editor;

namespace Forge.Language;

public sealed class CodeLensController(IEditorHost host, UiQueue ui)
{
	public const int ProvideDelayMilliseconds = 250;
	public const int ResolveDelayMilliseconds = 250;

	private readonly HashSet<(TextView, int)> _resolving = [];
	private readonly List<int> _visible = [];

	private TextView? _view;
	private string? _path;
	private int _version = -1;
	private int _providerVersion = -1;
	private float _scroll = -1f;
	private long _provideDue;
	private long _resolveDue;
	private int _generation;

	public void Tick(ILanguageFeatures? features, bool enabled, float rowHeight, float lineHeight) {
		if (host.GetActiveView() is not TextView view || host.GetActiveFilePath() is not { } path || host.GetActiveDocument() is not { } document) {
			_view = null;
			return;
		}

		if (!enabled || features is null || !features.Handles(path)) {
			view.SetCodeLenses([], rowHeight);
			return;
		}

		int version = document.GetBuffer().Version;
		int providerVersion = features.CodeLensVersion;
		long now = Environment.TickCount64;

		if (view != _view || path != _path || version != _version || providerVersion != _providerVersion) {
			_view = view;
			_path = path;
			_version = version;
			_providerVersion = providerVersion;
			_provideDue = now + ProvideDelayMilliseconds;
			_resolveDue = 0;
			_generation++;
		}

		if (view.ScrollY != _scroll) {
			_scroll = view.ScrollY;

			if (_resolveDue == 0 && _provideDue == 0) _resolveDue = now + ResolveDelayMilliseconds;
		}

		if (_provideDue != 0 && now >= _provideDue) {
			_provideDue = 0;
			Provide(features, view, path, version, rowHeight);
		}

		if (_resolveDue != 0 && now >= _resolveDue) {
			_resolveDue = 0;
			Resolve(features, view, path, lineHeight);
		}

		if (view.IsCodeLensFading) ui.Post(static () => { });
	}

	private void Provide(ILanguageFeatures features, TextView view, string path, int version, float rowHeight) {
		host.PublishActiveDocument();

		int generation = _generation;

		_ = Task.Run(async () => {
			IReadOnlyList<CodeLensEntry> lenses = await features.CodeLensesAsync(path).ConfigureAwait(false);

			ui.Post(() => {
				if (generation != _generation || view.Buffer.Version != version) return;

				Apply(view, lenses, rowHeight);
				_resolving.RemoveWhere(entry => entry.Item1 == view);
				_resolveDue = Environment.TickCount64;
			});
		});
	}

	private static void Apply(TextView view, IReadOnlyList<CodeLensEntry> lenses, float rowHeight) {
		CodeLensEntry[] sorted = [.. lenses];
		Array.Sort(sorted, static (left, right) => left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column));

		List<TextView.LensLine> lines = [];
		int start = 0;

		for (int i = 1; i <= sorted.Length; i++) {
			if (i < sorted.Length && sorted[i].Line == sorted[start].Line) continue;

			TextView.LensSegment[] segments = new TextView.LensSegment[i - start];

			for (int s = 0; s < segments.Length; s++) {
				CodeLensEntry entry = sorted[start + s];
				segments[s] = new TextView.LensSegment(entry.Title ?? string.Empty, entry.Title is not null, entry.Index);
			}

			lines.Add(new TextView.LensLine(sorted[start].Line - 1, segments));
			start = i;
		}

		view.SetCodeLenses(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), rowHeight);
	}

	private void Resolve(ILanguageFeatures features, TextView view, string path, float lineHeight) {
		view.VisibleCodeLenses(_visible, lineHeight);

		int generation = _generation;

		foreach (int index in _visible) {
			if (!_resolving.Add((view, index))) continue;

			_ = Task.Run(async () => {
				CodeLensTitle? title = await features.ResolveCodeLensAsync(path, index).ConfigureAwait(false);

				ui.Post(() => {
					if (generation != _generation) {
						_resolving.Remove((view, index));
						return;
					}

					view.ResolveCodeLens(index, title?.Text ?? "no commands", title?.IsCommand ?? false);
				});
			});
		}
	}

	public void Execute(ILanguageFeatures features, int index) {
		if (_path is not { } path) return;

		_ = Task.Run(async () => {
			try {
				if (await features.ExecuteCodeLensAsync(path, index).ConfigureAwait(false) is not { } references) return;

				ui.Post(() => {
					if (references.Locations.Count == 0) return;

					host.PeekReferences("References", references.Line, references.Column, references.Locations);
				});
			}
			catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Text.Json.JsonException) {
			}
		});
	}
}
