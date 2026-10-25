using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Common.Workspace;
using Forge.Theming;

namespace Forge.Search;

public sealed class QuickOpen(Action<string> open) : IQuickPickProvider
{
	private const int MaxResults = 64;
	private const int MaxQuery = 256;

	private readonly FuzzyMatch[] _matches = new FuzzyMatch[MaxResults];

	public const int HistoryLimit = 200;

	private readonly List<string> _rows = [];

	private IFileIndex? _index;
	private int _recentCount;

	public List<string> History { get; set; } = [];

	public void Attach(IFileIndex index) => _index = index;

	public void Touch(string path) {
		History.RemoveAll(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase));
		History.Insert(0, path);

		if (History.Count > HistoryLimit) History.RemoveRange(HistoryLimit, History.Count - HistoryLimit);
	}

	public string GetPlaceholder() => "Search files by name";

	public string GetEmptyMessage() => "No matching results";

	public int GetCount() => _rows.Count;

	public void Filter(ReadOnlySpan<char> query) {
		_rows.Clear();
		_recentCount = 0;

		if (_index is null) return;

		string root = _index.GetRootPath();
		query = query[..Math.Min(query.Length, MaxQuery)];

		foreach (string path in History) {
			if (_rows.Count >= MaxResults) break;

			string relative = Path.GetRelativePath(root, path);

			if (query.IsEmpty || FuzzyMatcher.TryMatch(query, relative, out _)) _rows.Add(relative);
		}

		_recentCount = _rows.Count;

		if (query.IsEmpty) return;

		string[] files = _index.GetFiles();
		int count = FuzzyMatcher.Rank(query, files, _matches);

		for (int i = 0; i < count; i++) {
			if (_matches[i].Index >= files.Length) continue;

			string file = files[_matches[i].Index];

			if (_rows.FindIndex(0, _recentCount, row => string.Equals(row, file, StringComparison.OrdinalIgnoreCase)) < 0) _rows.Add(file);
		}
	}

	public void Accept(int index) {
		if (_index is null || (uint)index >= (uint)_rows.Count) return;

		open(Path.GetFullPath(Path.Combine(_index.GetRootPath(), _rows[index])));
	}

	public void PaintItem(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		if ((uint)index >= (uint)_rows.Count) return;

		string? separator = index == 0 && _recentCount > 0 ? "recently opened" : index == _recentCount && index > 0 ? "file results" : null;

		if (separator is not null) {
			ITextFont label = painter.GetUi();
			float labelBaseline = top + ((height - label.GetLineHeight()) / 2f) + label.GetBaseline();
			painter.DrawText(label, separator, left + width - (Metrics.Space12 * Panel.Scale) - label.Measure(separator), labelBaseline, Theme.DescriptionForeground);

			if (index > 0) painter.FillRectangle(left, top, width, Metrics.StrokeThickness * Panel.Scale, Theme.WidgetBorder);
		}

		ReadOnlySpan<char> relative = _rows[index];
		int nameStart = relative.LastIndexOfAny('/', '\\') + 1;
		ReadOnlySpan<char> name = relative[nameStart..];

		float scale = Panel.Scale;
		ITextFont font = painter.GetUi();
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float icon = Metrics.IconSize * scale;
		float cursor = left + (Metrics.Space12 * scale);

		if (painter.DrawFileIcon(name, cursor + (icon / 2f), top + (height / 2f), icon)) cursor += icon + (Metrics.Space6 * scale);

		painter.DrawText(font, name, cursor, baseline, selected ? Theme.ListHoverForeground : Theme.Foreground);

		if (nameStart <= 1) return;

		painter.DrawText(font, relative[..(nameStart - 1)], cursor + font.Measure(name) + (Metrics.Space8 * scale), baseline, Theme.DescriptionForeground);
	}
}
