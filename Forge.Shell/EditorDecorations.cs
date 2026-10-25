using System.Buffers;

using Forge.Editor;
using Forge.Extensions.IndentRainbow;
using Forge.Extensions.TrailingSpaces;
using Forge.Common.Commands;
using Forge.Text;
using Forge.Theming;

namespace Forge.Shell;

public sealed class EditorDecorations
{
	private const uint TrailingColor = 0x4DFF0000;

	private readonly List<TextView.Decoration> _decorations = [];
	private readonly List<IndentSpan> _indents = [];

	private IReadOnlyList<DocumentHighlight>? _highlights;

	private char[] _line = ArrayPool<char>.Shared.Rent(256);
	private TextView? _view;
	private int _version = -1;
	private int _first = -1;
	private int _last = -1;
	private bool _includeEmptyLines;

	public bool IndentRainbowEnabled { get; set; } = true;

	public bool TrailingSpacesEnabled { get; set; } = true;

	public void UpdateVisible(TextView? view, TrailingSpacesExtension trailing, IReadOnlyList<DocumentHighlight> highlights, int firstLine, int lastLine) {
		if (ReferenceEquals(view, _view) && view is not null && view.Buffer.Version == _version && firstLine == _first && lastLine == _last && trailing.IncludeEmptyLines == _includeEmptyLines && ReferenceEquals(highlights, _highlights)) return;

		_decorations.Clear();
		_highlights = highlights;
		_view = view;
		_version = view?.Buffer.Version ?? -1;
		_first = firstLine;
		_last = lastLine;
		_includeEmptyLines = trailing.IncludeEmptyLines;

		if (view is null) return;

		view.Decorations = _decorations;

		TextSnapshot snapshot = view.Snapshot;
		int last = Math.Min(lastLine, snapshot.LineCount - 1);

		for (int line = Math.Max(0, firstLine); line <= last; line++) {
			TextSpan span = snapshot.GetLineSpan(line);

			if (span.Length > _line.Length) {
				ArrayPool<char>.Shared.Return(_line);
				_line = ArrayPool<char>.Shared.Rent(span.Length);
			}

			Span<char> text = _line.AsSpan(0, span.Length);
			snapshot.CopyTo(span.Start, span.Length, text);

			if (IndentRainbowEnabled) {
				_indents.Clear();
				IndentRainbow.ScanLine(text, line, view.TabWidth, IndentRainbow.DefaultColors.Length, _indents);

				foreach (IndentSpan indent in _indents) _decorations.Add(new TextView.Decoration(line, indent.Start, indent.End, IndentRainbow.ColorOf(indent, IndentRainbow.DefaultColors, IndentRainbow.DefaultErrorColor, tabMixColor: 0u)));
			}

			if (TrailingSpacesEnabled && TrailingSpacesExtension.FindLine(text, line, _includeEmptyLines, out TrailingRange range)) _decorations.Add(new TextView.Decoration(line, range.Start, range.End, TrailingColor));

			foreach (DocumentHighlight highlight in highlights) {
				if (highlight.Line - 1 > line || highlight.EndLine - 1 < line) continue;

				int start = highlight.Line - 1 == line ? highlight.StartColumn - 1 : 0;
				int end = highlight.EndLine - 1 == line ? highlight.EndColumn - 1 : span.Length;

				_decorations.Add(new TextView.Decoration(line, start, end, ColorOf(highlight.Kind)));
			}
		}
	}

	private static uint ColorOf(DocumentHighlightKind kind) => (kind switch {
		DocumentHighlightKind.Write => Theme.WordHighlightStrongBackground,
		DocumentHighlightKind.Read => Theme.WordHighlightBackground,
		_ => Theme.WordHighlightTextBackground,
	}).ToArgb();
}
