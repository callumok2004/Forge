using Forge.Editor;
using Forge.Extensions.IndentRainbow;
using Forge.Extensions.TrailingSpaces;
using Forge.Extensions;

namespace Forge.Shell;

public sealed class EditorDecorations
{
	private readonly List<TextView.Decoration> _decorations = [];

	public bool IndentRainbowEnabled { get; set; } = true;

	public bool TrailingSpacesEnabled { get; set; } = true;

	private const uint TrailingColor = 0x4DFF0000;

	private TextView? _view;
	private int _version = -1;

	public void UpdateIfStale(TextView? view, TrailingSpacesExtension trailing) {
		if (ReferenceEquals(view, _view) && view is not null && view.Buffer.Version == _version) return;

		Update(view, trailing);
	}

	public void Update(TextView? view, TrailingSpacesExtension trailing) {
		_decorations.Clear();
		_view = view;
		_version = view?.Buffer.Version ?? -1;

		if (view is null) return;

		view.Decorations = _decorations;

		string text = view.Buffer.GetText();

		if (IndentRainbowEnabled) {
			foreach (IndentSpan span in IndentRainbow.Scan(text, view.TabWidth,
				IndentRainbow.DefaultColors.Length)) {
				uint color = IndentRainbow.ColorOf(span, IndentRainbow.DefaultColors,
					IndentRainbow.DefaultErrorColor, tabMixColor: 0u);

				_decorations.Add(new TextView.Decoration(span.Line, span.Start, span.End, color));
			}
		}

		if (TrailingSpacesEnabled) {
			foreach (TrailingRange range in TrailingSpacesExtension.Find(text, skipLine: null,
				trailing.IncludeEmptyLines)) {
				_decorations.Add(new TextView.Decoration(range.Line, range.Start, range.End, TrailingColor));
			}
		}

		_decorations.Sort(static (left, right) => left.Line.CompareTo(right.Line));
	}
}
