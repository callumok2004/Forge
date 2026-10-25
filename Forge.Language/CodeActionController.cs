using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Text;

namespace Forge.Language;

public sealed class CodeActionController(IEditorHost workbench, UiQueue ui, EditorMessageWidget message)
{
	public const int DelayMilliseconds = 250;
	private const float SpaceThreshold = 22f;

	public readonly record struct Lightbulb(int Line, int Column, bool InGutter, bool AutoFix, int AnchorOffset);

	private string? _path;
	private int _version = -1;
	private int _anchor = -1;
	private int _caret = -1;
	private long _due;
	private int _generation;
	private IReadOnlyList<CodeActionEntry> _actions = [];

	public CodeActionWidget Widget { get; } = new();

	public Lightbulb? Current { get; private set; }

	public Action? Changed { get; set; }

	public (float X, float Y) MenuAnchor { get; set; }

	public void Tick(ILanguageFeatures features, float cellWidth, float scale) {
		if (workbench.GetActiveFilePath() is not { } path || workbench.GetActiveView() is not { } view || workbench.GetActiveDocument() is not { } document) {
			Clear();
			return;
		}

		int version = document.GetBuffer().Version;
		int caret = view.GetCaretOffset();
		int anchor = caret - view.GetSelectionLength();

		if (path != _path || version != _version || caret != _caret || anchor != _anchor) {
			_path = path;
			_version = version;
			_caret = caret;
			_anchor = anchor;
			_due = Environment.TickCount64 + DelayMilliseconds;
			_generation++;
			Clear();
			return;
		}

		if (_due == 0 || Environment.TickCount64 < _due) return;

		_due = 0;

		Request(features, automatic: true, (actions, generation) => {
			if (generation != _generation) return;

			_actions = actions;
			Place(view, actions, cellWidth, scale);
		});
	}

	public void Invoke(ILanguageFeatures features) {
		if (workbench.GetActiveView() is not { } view) return;

		int offset = view.GetCaretOffset();
		(float x, float y) = MenuAnchor;

		Request(features, automatic: false, (actions, _) => {
			if (actions.Count == 0) {
				message.Show("No code actions available", offset);
				return;
			}

			Open(features, actions, x, y);
		});
	}

	public void OpenFromLightbulb(ILanguageFeatures features, float x, float y) {
		if (_actions.Count > 0) Open(features, _actions, x, y);
	}

	private void Open(ILanguageFeatures features, IReadOnlyList<CodeActionEntry> actions, float x, float y) {
		Widget.Accepted = index => _ = features.ExecuteCodeActionAsync(index);
		Widget.Closed = workbench.RestoreEditorFocus;
		Widget.Show(actions, x, y);
	}

	private void Request(ILanguageFeatures features, bool automatic, Action<IReadOnlyList<CodeActionEntry>, int> done) {
		if (workbench.GetActiveFilePath() is not { } path || workbench.GetActiveView() is not { } view || !features.Handles(path)) return;

		workbench.PublishActiveDocument();

		TextSnapshot snapshot = view.GetSnapshot();
		int caret = view.GetCaretOffset();
		int anchor = caret - view.GetSelectionLength();
		int start = Math.Min(caret, anchor);
		int end = Math.Max(caret, anchor);
		int startLine = snapshot.GetLineAt(start);
		int endLine = snapshot.GetLineAt(end);
		int generation = _generation;

		_ = Task.Run(async () => {
			IReadOnlyList<CodeActionEntry> actions = await features.CodeActionsAsync(path, startLine + 1, start - snapshot.GetLineStartOffset(startLine) + 1, endLine + 1, end - snapshot.GetLineStartOffset(endLine) + 1, automatic).ConfigureAwait(false);

			ui.Post(() => done(actions, generation));
		});
	}

	private void Place(ITextView view, IReadOnlyList<CodeActionEntry> actions, float cellWidth, float scale) {
		bool any = false;
		bool autoFix = false;

		foreach (CodeActionEntry action in actions) {
			if (action.DisabledReason is not null) continue;

			any = true;
			autoFix |= action.IsPreferred && (action.Kind == "quickfix" || action.Kind.StartsWith("quickfix.", StringComparison.Ordinal));
		}

		if (!any) {
			Clear();
			return;
		}

		TextSnapshot snapshot = view.GetSnapshot();
		int caret = view.GetCaretOffset();
		int line = snapshot.GetLineAt(caret);
		int column = caret - snapshot.GetLineStartOffset(line);
		int lineCount = snapshot.LineCount;
		bool hasSpace = cellWidth * IndentLevel(snapshot, line, view.GetTabWidth()) > SpaceThreshold * scale;

		Lightbulb At(int targetLine, bool gutter) => new(targetLine, gutter ? 0 : FirstColumn(snapshot, targetLine), gutter, autoFix, caret);

		if (hasSpace) {
			Show(At(line, gutter: false));
			return;
		}

		bool EmptyOrIndented(int target) {
			string text = LineText(snapshot, target);
			return text.Length == 0 || char.IsWhiteSpace(text[0]) || text.Length <= 1;
		}

		if (line > 0) {
			bool endLine = line == lineCount - 1;
			bool previous = EmptyOrIndented(line - 1);
			bool next = !endLine && EmptyOrIndented(line + 1);
			bool current = EmptyOrIndented(line);

			if (!next && !previous) {
				Show(At(line, gutter: true));
				return;
			}

			if (previous || endLine) Show(At(line - 1, gutter: false));
			else if (next || current) Show(At(line + 1, gutter: false));
			else Show(At(line, gutter: false));

			return;
		}

		if (lineCount == 1 || (!EmptyOrIndented(line + 1) && !EmptyOrIndented(line))) {
			Show(At(line, gutter: true));
			return;
		}

		if (line < lineCount - 1) {
			Show(At(line + 1, gutter: false));
			return;
		}

		if (column * cellWidth < SpaceThreshold * scale) {
			Clear();
			return;
		}

		Show(At(line, gutter: false));
	}

	private static string LineText(TextSnapshot snapshot, int line) {
		TextSpan span = snapshot.GetLineSpan(line);
		return snapshot.GetText(span.Start, span.Length);
	}

	private static int FirstColumn(TextSnapshot snapshot, int line) {
		string text = LineText(snapshot, line);
		return text.Length >= 1 && !char.IsWhiteSpace(text[0]) && text.AsSpan(1).IsWhiteSpace() ? 1 : 0;
	}

	private static int IndentLevel(TextSnapshot snapshot, int line, int tabSize) {
		string text = LineText(snapshot, line);
		int level = 0;

		foreach (char character in text) {
			if (character == ' ') level++;
			else if (character == '\t') level += tabSize - (level % tabSize);
			else return level;
		}

		return level;
	}

	private void Show(Lightbulb lightbulb) {
		if (Current == lightbulb) return;

		Current = lightbulb;
		Changed?.Invoke();
	}

	public void Clear() {
		_actions = [];

		if (Current is null) return;

		Current = null;
		Changed?.Invoke();
	}
}
