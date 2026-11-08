using Forge.Text;
using Forge.Theming;

namespace Forge.Editor;

public sealed class DiffMarks(bool removed)
{
	private readonly List<(int Start, int Count)> _lines = [];

	public bool Removed { get; } = removed;

	public Color LineBackground { get; } = removed ? Theme.DiffRemovedLineBackground : Theme.DiffInsertedLineBackground;

	public Color TextBackground { get; } = removed ? Theme.DiffRemovedTextBackground : Theme.DiffInsertedTextBackground;

	public List<CharRange> Chars { get; } = [];

	public void Clear() {
		_lines.Clear();
		Chars.Clear();
	}

	public void AddLines(int start, int count) {
		if (count > 0) _lines.Add((start, count));
	}

	public void SortChars() => Chars.Sort(static (a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Start.CompareTo(b.Start));

	public bool Contains(int line) {
		int low = 0;
		int high = _lines.Count - 1;

		while (low <= high) {
			int middle = (low + high) >> 1;
			(int start, int count) = _lines[middle];

			if (line < start) high = middle - 1;
			else if (line >= start + count) low = middle + 1;
			else return true;
		}

		return false;
	}

	public int FirstChar(int line) {
		int low = 0;
		int high = Chars.Count;

		while (low < high) {
			int middle = (low + high) >> 1;

			if (Chars[middle].Line < line) low = middle + 1;
			else high = middle;
		}

		return low;
	}
}
