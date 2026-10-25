using System.Buffers;

using Forge.Editor;
using Forge.Extensions.IndentRainbow;
using Forge.Extensions.TrailingSpaces;
using Forge.Text;

namespace Forge.Shell;

public sealed class EditorDecorations
{
	private const uint TrailingColor = 0x4DFF0000;

	private readonly List<TextView.Decoration> _decorations = [];
	private readonly List<IndentSpan> _indents = [];

	private char[] _line = ArrayPool<char>.Shared.Rent(256);
	private TextView? _view;
	private int _version = -1;
	private int _first = -1;
	private int _last = -1;
	private bool _includeEmptyLines;

	public bool IndentRainbowEnabled { get; set; } = true;

	public bool TrailingSpacesEnabled { get; set; } = true;

	public void UpdateVisible(TextView? view, TrailingSpacesExtension trailing, int firstLine, int lastLine) {
		if (ReferenceEquals(view, _view) && view is not null && view.Buffer.Version == _version && firstLine == _first && lastLine == _last && trailing.IncludeEmptyLines == _includeEmptyLines) return;

		_decorations.Clear();
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
		}
	}
}
