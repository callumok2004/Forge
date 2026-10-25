using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Text;

namespace Forge.Language;

public sealed class SuggestSession(IEditorHost workbench, SuggestWidget widget, UiQueue ui)
{
	private sealed record LineContext(int Line, int Column, string Leading, int WordStart, int WordLength);

	private LineContext? _context;
	private bool _auto;
	private bool _incomplete;
	private bool _hasModel;
	private int _generation;

	public bool IsActive => _context is not null;

	public Action? Shown { get; set; }

	public void Typed(ILanguageFeatures features, char character) {
		LanguageTriggers triggers = features.GetTriggers();

		if (triggers.Suggest.Contains(character) && character != ' ') {
			Trigger(features, auto: true, character, CompletionTrigger.Character);
			return;
		}

		if (_context is not null) {
			OnNewContext(features);
			return;
		}

		if (CurrentContext() is { WordLength: > 0 }) Trigger(features, auto: true, '\0', CompletionTrigger.Invoked);
	}

	public void CursorChanged(ILanguageFeatures features) {
		if (_context is not null) OnNewContext(features);
	}

	public void Invoke(ILanguageFeatures features) => Trigger(features, auto: false, '\0', CompletionTrigger.Invoked);

	public void Cancel() {
		_context = null;
		_hasModel = false;
		_incomplete = false;
		_generation++;
		widget.Hide();
	}

	private LineContext? CurrentContext() {
		if (workbench.GetActiveView() is not { } view) return null;

		TextSnapshot snapshot = view.GetSnapshot();
		int line = view.GetCaretLine();
		TextSpan span = snapshot.GetLineSpan(line);
		int caret = Math.Clamp(view.GetCaretOffset(), span.Start, span.End);
		string leading = snapshot.GetText(span.Start, caret - span.Start);
		int start = leading.Length;

		while (start > 0 && (char.IsLetterOrDigit(leading[start - 1]) || leading[start - 1] == '_')) start--;

		return new LineContext(line + 1, leading.Length + 1, leading, start + 1, leading.Length - start);
	}

	private static int LeadingWhitespace(string text) {
		int index = 0;

		while (index < text.Length && text[index] is ' ' or '\t') index++;

		return index;
	}

	private void Trigger(ILanguageFeatures features, bool auto, char trigger, CompletionTrigger kind) {
		if (workbench.GetActiveFilePath() is not { } path || CurrentContext() is not { } context) return;

		workbench.PublishActiveDocument();

		int generation = ++_generation;

		_context = context;
		_auto = auto;
		_hasModel = false;

		_ = Task.Run(async () => {
			CompletionList list = await features.CompletionsAsync(path, context.Line, context.Column, trigger, kind).ConfigureAwait(false);

			ui.Post(() => Arrived(features, generation, context, list));
		});
	}

	private void Arrived(ILanguageFeatures features, int generation, LineContext requested, CompletionList list) {
		if (generation != _generation || _context is null) return;

		_hasModel = true;
		_incomplete = list.IsIncomplete;

		if (list.Items.Count == 0) {
			if (_auto) Cancel();

			return;
		}

		if (CurrentContext() is not { } now) return;

		widget.Show(FillInRanges(list.Items, requested), requested.Line, requested.Column, now.Leading, now.Column);
		Shown?.Invoke();
		OnNewContext(features);
	}

	private void OnNewContext(ILanguageFeatures features) {
		if (_context is not { } context || CurrentContext() is not { } now) return;

		if (now.Line != context.Line || LeadingWhitespace(now.Leading) != LeadingWhitespace(context.Leading)) {
			Cancel();
			return;
		}

		if (now.Column < context.Column) {
			if (now.WordLength > 0) Trigger(features, _auto, '\0', CompletionTrigger.Invoked);
			else Cancel();

			return;
		}

		if (!_hasModel) return;

		if (now.WordLength > 0 && now.WordStart > context.WordStart) {
			Trigger(features, _auto, '\0', CompletionTrigger.Invoked);
			return;
		}

		if (now.Column > context.Column && _incomplete && now.WordLength > 0) {
			Trigger(features, _auto, '\0', CompletionTrigger.Incomplete);
			return;
		}

		widget.Filter(now.Leading, now.Column);

		if (widget.IsOpen) return;

		if (_auto || now.WordLength == 0) Cancel();
	}

	private static CompletionEntry[] FillInRanges(IReadOnlyList<CompletionEntry> items, LineContext context) {
		CompletionEntry[] filled = new CompletionEntry[items.Count];

		for (int i = 0; i < items.Count; i++) filled[i] = items[i].ReplaceStartColumn > 0 ? items[i] : items[i] with { ReplaceStartColumn = context.WordStart, ReplaceEndColumn = context.Column };

		return filled;
	}
}
