using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Shell;

public sealed class GoToLineProvider(Workbench workbench) : IQuickPickProvider
{
	private string _label = string.Empty;
	private int _line;
	private int _column;

	public string GetPlaceholder() => string.Empty;

	public string GetEmptyMessage() => string.Empty;

	public int GetCount() => 1;

	public void Filter(ReadOnlySpan<char> query) {
		_line = 0;
		_column = 1;

		if (workbench.ActiveView is not { } view || workbench.ActiveDocument?.Path is not { } path) {
			_label = "Open a text editor first to go to a line or an offset.";
			return;
		}

		int maxLine = view.Snapshot.LineCount;
		ReadOnlySpan<char> value = query.Trim();

		if (!value.IsEmpty && value[0] == ':') value = value[1..].Trim();

		int separator = value.IndexOfAny(',', ':', '#');
		ReadOnlySpan<char> linePart = (separator < 0 ? value : value[..separator]).Trim();

		if (!int.TryParse(linePart, out int line)) {
			_label = $"Type a line number to go to (from 1 to {maxLine}).";
			return;
		}

		line = line >= 0 ? line : maxLine + 1 + line;
		line = Math.Clamp(line, 1, maxLine);
		_line = line;

		int maxColumn = view.Snapshot.GetLineSpan(line - 1).Length + 1;

		if (separator < 0 || !int.TryParse(value[(separator + 1)..].Trim(), out int column)) {
			_label = separator < 0
				? $"Press 'Enter' to go to line {line} or enter colon : to add a column number."
				: $"Press 'Enter' to go to line {line} or enter a column number (from 1 to {maxColumn}).";
			return;
		}

		column = column >= 0 ? column : maxColumn + column;
		_column = Math.Clamp(column, 1, maxColumn);
		_label = $"Press 'Enter' to go to line {line} at column {_column}.";
	}

	public void Accept(int index) {
		if (_line <= 0 || workbench.ActiveDocument?.Path is not { } path) return;

		workbench.GoTo(path, _line, _column);
	}

	public void PaintItem(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		ITextFont font = painter.GetUi();
		float padding = Metrics.Space8 * Panel.Scale;
		painter.DrawText(font, _label, left + padding + padding, top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline(), selected ? Theme.ListHoverForeground : Theme.Foreground);
	}
}
