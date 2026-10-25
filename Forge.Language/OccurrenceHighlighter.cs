using Forge.Common.Commands;
using Forge.Common.Editor;

namespace Forge.Language;

public sealed class OccurrenceHighlighter(IEditorHost host, Func<ILanguageFeatures?> features, Action refresh)
{
	public const int DelayMilliseconds = 250;

	private sealed record Result(string Path, int Version, IReadOnlyList<DocumentHighlight> Highlights);

	private volatile Result? _result;
	private string? _path;
	private int _version = -1;
	private int _caret = -1;
	private long _due;
	private int _generation;

	public IReadOnlyList<DocumentHighlight> For(string? path, int version) => _result is { } result && result.Path == path && result.Version == version ? result.Highlights : [];

	public void Tick() {
		if (host.GetActiveFilePath() is not { } path || host.GetActiveView() is not { } view || host.GetActiveDocument() is not { } document) {
			_path = null;
			return;
		}

		int version = document.GetBuffer().Version;
		int caret = view.GetCaretOffset();

		if (path != _path || version != _version || caret != _caret) {
			if (path != _path || version != _version || !Covers(view, caret)) _result = null;

			_path = path;
			_version = version;
			_caret = caret;
			_due = Environment.TickCount64 + DelayMilliseconds;
			_generation++;
			return;
		}

		if (_due == 0 || Environment.TickCount64 < _due) return;

		_due = 0;

		if (features() is not { } provider || !provider.Handles(path) || view.GetSelectionLength() > 0) return;

		host.PublishActiveDocument();

		int generation = _generation;
		int line = view.GetCaretLine() + 1;
		int column = view.GetCaretColumn() + 1;

		_ = Task.Run(async () => {
			IReadOnlyList<DocumentHighlight> highlights = await provider.DocumentHighlightsAsync(path, line, column).ConfigureAwait(false);

			if (generation != _generation) return;

			_result = new Result(path, version, highlights);
			refresh();
		});
	}

	private bool Covers(ITextView view, int caret) {
		if (_result is not { } result) return false;

		int line = view.GetCaretLine() + 1;
		int column = caret - view.GetSnapshot().GetLineStartOffset(view.GetCaretLine()) + 1;

		foreach (DocumentHighlight highlight in result.Highlights) {
			if (highlight.Line == line && column >= highlight.StartColumn && column <= highlight.EndColumn) return true;
		}

		return false;
	}
}
