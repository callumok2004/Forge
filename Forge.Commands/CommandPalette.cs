using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Search;
using Forge.Theming;

namespace Forge.Commands;

public sealed class CommandPalette(ICommandRegistry registry, Action<ICommand> run) : IQuickPickProvider
{
	private readonly List<(ICommand Command, int Score)> _matches = [];

	public string GetPlaceholder() => "Type the name of a command to run.";

	public string GetEmptyMessage() => "No matching commands";

	public int GetCount() => _matches.Count;

	public void Filter(ReadOnlySpan<char> query) {
		_matches.Clear();

		foreach (ICommand command in registry.All()) {
			if (!command.IsEnabled()) continue;

			if (query.IsEmpty) {
				_matches.Add((command, 0));
				continue;
			}

			bool label = FuzzyMatcher.TryMatch(query, command.GetLabel(), out int labelScore);
			bool id = FuzzyMatcher.TryMatch(query, command.GetId(), out int idScore);

			if (label || id) _matches.Add((command, Math.Max(label ? labelScore : int.MinValue, id ? idScore : int.MinValue)));
		}

		if (!query.IsEmpty) _matches.Sort(static (left, right) => right.Score.CompareTo(left.Score));
	}

	public void Accept(int index) {
		if ((uint)index < (uint)_matches.Count) run(_matches[index].Command);
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

		if (command.GetKeybinding() is not { Length: > 0 } keys) return;

		int split = keys.IndexOf('|');
		ReadOnlySpan<char> first = split < 0 ? keys : keys.AsSpan(0, split);
		painter.DrawText(font, first, left + width - padding - padding - font.Measure(first), baseline, Theme.DescriptionForeground);
	}
}
