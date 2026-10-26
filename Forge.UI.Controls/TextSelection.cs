using System.Text;

using Forge.Common.Render;
using Forge.Theming;

namespace Forge.UI.Controls;

public readonly record struct TextRun(float X, float Y, string Text, ITextFont Font, Color Color, bool Joined, bool Selectable = true);

public sealed class TextSelection
{
	private readonly List<TextRun> _runs = [];
	private (int Run, int Offset) _anchor = (-1, 0);
	private (int Run, int Offset) _focus = (-1, 0);

	public IReadOnlyList<TextRun> Runs => _runs;

	public bool HasSelection => _anchor.Run >= 0 && _focus.Run >= 0 && _anchor != _focus;

	public void Clear() {
		_runs.Clear();
		ClearSelection();
	}

	public void ClearSelection() {
		_anchor = (-1, 0);
		_focus = (-1, 0);
	}

	public void Add(TextRun run) => _runs.Add(run);

	public bool OverText(float x, float y) {
		foreach (TextRun run in _runs) {
			if (run.Selectable && y >= run.Y && y < run.Y + run.Font.GetLineHeight() && x >= run.X && x < run.X + run.Font.Measure(run.Text)) return true;
		}

		return false;
	}

	public (int Run, int Offset) PositionAt(float x, float y) {
		int best = -1;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < _runs.Count; i++) {
			TextRun run = _runs[i];

			if (!run.Selectable) continue;

			float height = run.Font.GetLineHeight();
			float width = run.Font.Measure(run.Text);
			float dy = y < run.Y ? run.Y - y : y > run.Y + height ? y - run.Y - height : 0f;
			float dx = x < run.X ? run.X - x : x > run.X + width ? x - run.X - width : 0f;
			float distance = (dy * 1000f) + dx;

			if (distance >= bestDistance) continue;

			bestDistance = distance;
			best = i;
		}

		if (best < 0) return (-1, 0);

		TextRun hit = _runs[best];
		float cursor = hit.X;

		for (int c = 0; c < hit.Text.Length; c++) {
			float advance = hit.Font.GetAdvance(hit.Text[c]);

			if (x < cursor + (advance / 2f)) return (best, c);

			cursor += advance;
		}

		return (best, hit.Text.Length);
	}

	public void Begin(float x, float y, bool extend) {
		if (extend && _anchor.Run >= 0) {
			_focus = PositionAt(x, y);
			return;
		}

		_anchor = PositionAt(x, y);
		_focus = _anchor;
	}

	public void Extend(float x, float y) => _focus = PositionAt(x, y);

	public void SelectWord(float x, float y) {
		(int run, int offset) = PositionAt(x, y);

		if (run < 0) return;

		string text = _runs[run].Text;
		int start = offset;
		int end = offset;

		while (start > 0 && IsWordCharacter(text[start - 1])) start--;

		while (end < text.Length && IsWordCharacter(text[end])) end++;

		_anchor = (run, start);
		_focus = (run, end);
	}

	private static bool IsWordCharacter(char c) => !char.IsWhiteSpace(c) && c is not ('.' or '(' or ')' or ',' or ':' or '\'' or '"');

	public void SelectAll() {
		int first = _runs.FindIndex(static run => run.Selectable);
		int last = _runs.FindLastIndex(static run => run.Selectable);

		if (first < 0) return;

		_anchor = (first, 0);
		_focus = (last, _runs[last].Text.Length);
	}

	private ((int Run, int Offset) Start, (int Run, int Offset) End) Ordered() =>
		_anchor.Run < _focus.Run || (_anchor.Run == _focus.Run && _anchor.Offset <= _focus.Offset) ? (_anchor, _focus) : (_focus, _anchor);

	public string SelectedText() {
		if (!HasSelection) return string.Empty;

		((int startRun, int startOffset), (int endRun, int endOffset)) = Ordered();
		StringBuilder builder = new();
		int previous = -1;

		for (int i = startRun; i <= endRun; i++) {
			TextRun run = _runs[i];

			if (!run.Selectable) continue;

			int from = i == startRun ? startOffset : 0;
			int to = i == endRun ? endOffset : run.Text.Length;

			if (previous >= 0) builder.Append(run.Joined ? (run.Y == _runs[previous].Y ? " " : string.Empty) : "\n");

			builder.Append(run.Text.AsSpan(from, Math.Max(0, to - from)));
			previous = i;
		}

		return builder.ToString();
	}

	public void Paint(IPainter painter, float originX, float originY, float clipTop, float clipBottom, bool focused) {
		bool selection = HasSelection;
		((int Run, int Offset) start, (int Run, int Offset) end) = selection ? Ordered() : ((-1, 0), (-1, 0));
		Color highlight = focused ? Theme.SelectionBackground : Theme.SelectionBackground.WithAlpha(0.6f);

		for (int i = 0; i < _runs.Count; i++) {
			TextRun run = _runs[i];
			float y = originY + run.Y;
			float height = run.Font.GetLineHeight();

			if (y > clipBottom || y + height < clipTop) continue;

			if (selection && run.Selectable && i >= start.Run && i <= end.Run) {
				int from = i == start.Run ? start.Offset : 0;
				int to = i == end.Run ? end.Offset : run.Text.Length;

				if (to > from) painter.FillRectangle(originX + run.X + run.Font.Measure(run.Text.AsSpan(0, from)), y, run.Font.Measure(run.Text.AsSpan(from, to - from)), height, highlight);
			}

			painter.DrawText(run.Font, run.Text, originX + run.X, y + run.Font.GetBaseline(), run.Color);
		}
	}
}
