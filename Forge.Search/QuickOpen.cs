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

	private IFileIndex? _index;
	private string[] _files = [];
	private int _count;

	public void Attach(IFileIndex index) => _index = index;

	public string GetPlaceholder() => "Search files by name";

	public string GetEmptyMessage() => "No matching results";

	public int GetCount() => _count;

	public void Filter(ReadOnlySpan<char> query) {
		if (_index is null) {
			_count = 0;
			return;
		}

		_files = _index.GetFiles();
		_count = FuzzyMatcher.Rank(query[..Math.Min(query.Length, MaxQuery)], _files, _matches);
	}

	public void Accept(int index) {
		if (_index is null || (uint)index >= (uint)_count || _matches[index].Index >= _files.Length) return;

		open(Path.Combine(_index.GetRootPath(), _files[_matches[index].Index]));
	}

	public void PaintItem(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		int fileIndex = _matches[index].Index;

		if (fileIndex >= _files.Length) return;

		ReadOnlySpan<char> relative = _files[fileIndex];
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
