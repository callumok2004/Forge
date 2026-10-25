using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Search;
using Forge.Theming;

namespace Forge.Commands;

public sealed class CommandPalette(ICommandRegistry registry, Action<ICommand> run) : IQuickPickProvider
{
	public const int HistoryLimit = 50;

	private readonly List<(ICommand Command, int Recency)> _matches = [];

	private int _firstOther = -1;

	public List<string> History { get; set; } = [];

	public string GetPlaceholder() => "Type the name of a command to run.";

	public string GetEmptyMessage() => "No matching commands";

	public int GetCount() => _matches.Count;

	public void Filter(ReadOnlySpan<char> query) {
		_matches.Clear();

		foreach (ICommand command in registry.All()) {
			if (!command.IsEnabled()) continue;

			if (!query.IsEmpty && !FuzzyMatcher.TryMatch(query, command.GetLabel(), out _) && !FuzzyMatcher.TryMatch(query, command.GetId(), out _)) continue;

			int recency = History.IndexOf(command.GetId());
			_matches.Add((command, recency < 0 ? int.MaxValue : recency));
		}

		_matches.Sort(static (left, right) => left.Recency != right.Recency ? left.Recency.CompareTo(right.Recency) : string.Compare(left.Command.GetLabel(), right.Command.GetLabel(), StringComparison.CurrentCulture));
		_firstOther = _matches.FindIndex(static match => match.Recency == int.MaxValue);
	}

	public void Accept(int index) {
		if ((uint)index >= (uint)_matches.Count) return;

		ICommand command = _matches[index].Command;
		History.Remove(command.GetId());
		History.Insert(0, command.GetId());

		if (History.Count > HistoryLimit) History.RemoveRange(HistoryLimit, History.Count - HistoryLimit);

		run(command);
	}

	public void PaintItem(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		ICommand command = _matches[index].Command;
		ITextFont font = painter.GetUi();
		float padding = Metrics.Space8 * Panel.Scale;
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float cursor = left + padding + padding;

		if (command.GetCategory() is { Length: > 0 } category) {
			painter.DrawText(font, category, cursor, baseline, Theme.DescriptionForeground);
			cursor += font.Measure(category);
			painter.DrawText(font, ": ", cursor, baseline, Theme.DescriptionForeground);
			cursor += font.Measure(": ");
		}

		painter.DrawText(font, command.GetTitle(), cursor, baseline, selected ? Theme.ListHoverForeground : Theme.Foreground);

		float right = left + width - padding - padding;
		string? separator = index == 0 && _matches[0].Recency != int.MaxValue ? "recently used" : index == _firstOther && index > 0 ? "other commands" : null;

		if (separator is not null) {
			right -= font.Measure(separator);
			painter.DrawText(font, separator, right, baseline, Theme.DescriptionForeground);
			right -= padding;

			if (index == _firstOther) painter.FillRectangle(left, top, width, Metrics.StrokeThickness * Panel.Scale, Theme.WidgetBorder);
		}

		if (command.GetKeybinding() is not { Length: > 0 } keys) return;

		int split = keys.IndexOf('|');
		ReadOnlySpan<char> first = split < 0 ? keys : keys.AsSpan(0, split);
		painter.DrawText(font, first, right - font.Measure(first), baseline, Theme.DescriptionForeground);
	}
}
