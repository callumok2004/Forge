using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Text;

namespace Forge.Language;

public sealed class NavigationController(IEditorHost workbench, UiQueue ui, EditorMessageWidget message)
{
	public readonly record struct Link(string Path, int Line, int StartColumn, int EndColumn, DefinitionResult Definition);

	private int _linkGeneration;
	private (string Path, int Line, int WordStart)? _linkKey;

	public Link? Current { get; private set; }

	public Action? LinkChanged { get; set; }

	public void RevealDefinition(ILanguageFeatures features) => AtCaret(features, async (path, line, column) => {
		DefinitionResult? result = await features.DefinitionAsync(path, line, column).ConfigureAwait(false);
		return ("Definitions", result?.Locations ?? [], false, "No definition found");
	});

	public void GoToImplementation(ILanguageFeatures features) => AtCaret(features, async (path, line, column) => ("Implementations", await features.ImplementationsAsync(path, line, column).ConfigureAwait(false), false, "No implementation found"));

	public void FindReferences(ILanguageFeatures features) => AtCaret(features, async (path, line, column) => ("References", await features.ReferencesAsync(path, line, column).ConfigureAwait(false), true, "No references found"));

	private void AtCaret(ILanguageFeatures features, Func<string, int, int, Task<(string Title, IReadOnlyList<HostLocation> Found, bool AlwaysList, string Empty)>> request) {
		if (workbench.GetActiveFilePath() is not { } path || workbench.GetActiveView() is not { } view) return;

		workbench.PublishActiveDocument();

		int line = view.GetCaretLine() + 1;
		int column = view.GetCaretColumn() + 1;
		int offset = view.GetCaretOffset();

		_ = Task.Run(async () => {
			(string title, IReadOnlyList<HostLocation> found, bool alwaysList, string empty) = await request(path, line, column).ConfigureAwait(false);

			ui.Post(() => Open(title, found, alwaysList, empty, offset));
		});
	}

	private void Open(string title, IReadOnlyList<HostLocation> found, bool alwaysList, string empty, int offset) {
		if (found.Count == 0) {
			message.Show(empty, offset);
			return;
		}

		if (found.Count == 1 && !alwaysList) {
			workbench.GoTo(found[0].Path, found[0].Line, found[0].Column);
			return;
		}

		if (workbench.GetActiveView() is not { } view) return;

		int line = view.GetSnapshot().GetLineAt(offset);
		workbench.PeekReferences(title, line + 1, offset - view.GetSnapshot().GetLineStartOffset(line) + 1, found);
	}

	public void Hover(ILanguageFeatures features, int offset) {
		if (workbench.GetActiveFilePath() is not { } path || workbench.GetActiveView() is not { } view) return;

		TextSnapshot snapshot = view.GetSnapshot();
		int lineIndex = snapshot.GetLineAt(offset);
		TextSpan span = snapshot.GetLineSpan(lineIndex);
		string text = snapshot.GetText(span.Start, span.Length);
		int column = offset - span.Start;
		int start = column;
		int end = column;

		while (start > 0 && IsWord(text[start - 1])) start--;

		while (end < text.Length && IsWord(text[end])) end++;

		if (start == end) {
			Clear();
			return;
		}

		(string, int, int) key = (path, lineIndex + 1, start + 1);

		if (_linkKey == key) return;

		Clear();
		_linkKey = key;

		workbench.PublishActiveDocument();

		int generation = ++_linkGeneration;
		int line = lineIndex + 1;

		_ = Task.Run(async () => {
			DefinitionResult? result = await features.DefinitionAsync(path, line, column + 1).ConfigureAwait(false);

			ui.Post(() => {
				if (generation != _linkGeneration || result is not { Locations.Count: > 0 }) return;

				Current = result.HasOrigin ? new Link(path, result.OriginLine, result.OriginStartColumn, result.OriginEndColumn, result) : new Link(path, line, start + 1, end + 1, result);
				LinkChanged?.Invoke();
			});
		});
	}

	public bool Click(int offset) {
		if (Current is not { } link || workbench.GetActiveView() is not { } view || workbench.GetActiveFilePath() != link.Path) return false;

		TextSnapshot snapshot = view.GetSnapshot();
		int line = snapshot.GetLineAt(offset) + 1;
		int column = offset - snapshot.GetLineStartOffset(line - 1) + 1;

		if (line != link.Line || column < link.StartColumn || column > link.EndColumn) return false;

		IReadOnlyList<HostLocation> found = link.Definition.Locations;

		Clear();
		Open("Definitions", found, alwaysList: false, "No definition found", offset);
		return true;
	}

	public void Clear() {
		_linkGeneration++;
		_linkKey = null;

		if (Current is null) return;

		Current = null;
		LinkChanged?.Invoke();
	}

	private static bool IsWord(char character) => char.IsLetterOrDigit(character) || character == '_';
}
