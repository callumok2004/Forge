using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Search;
using Forge.Theming;

namespace Forge.Commands;

public sealed class ListQuickPick(string placeholder, IReadOnlyList<QuickPickItem> items, Action<int> picked) : IQuickPickProvider
{
	private readonly List<(int Index, int Score)> _matches = [with(items.Count)];

	public string GetPlaceholder() => placeholder;

	public string GetEmptyMessage() => "No matching results";

	public int GetCount() => _matches.Count;

	public void Filter(ReadOnlySpan<char> query) {
		_matches.Clear();

		for (int i = 0; i < items.Count; i++) {
			if (query.IsEmpty) {
				_matches.Add((i, 0));
				continue;
			}

			bool label = FuzzyMatcher.TryMatch(query, items[i].Label, out int labelScore);
			bool description = FuzzyMatcher.TryMatch(query, items[i].Description, out int descriptionScore);

			if (label || description) _matches.Add((i, Math.Max(label ? labelScore : int.MinValue, description ? descriptionScore - 1 : int.MinValue)));
		}

		if (!query.IsEmpty) _matches.Sort(static (left, right) => right.Score.CompareTo(left.Score));
	}

	public void Accept(int index) => picked((uint)index < (uint)_matches.Count ? _matches[index].Index : -1);

	public void Cancel() => picked(-1);

	public void PaintItem(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		QuickPickItem item = items[_matches[index].Index];
		ITextFont font = painter.GetUi();
		float padding = Metrics.Space8 * Panel.Scale;
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float cursor = left + padding + padding;

		painter.DrawText(font, item.Label, cursor, baseline, selected ? Theme.ListHoverForeground : Theme.Foreground);

		if (item.Description.Length == 0) return;

		cursor += font.Measure(item.Label) + padding;
		painter.DrawText(font, item.Description, cursor, baseline, Theme.DescriptionForeground);
	}
}
