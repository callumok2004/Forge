using System.Text;

using Forge.Common.Render;
using Forge.Theming;

namespace Forge.Editor;

public sealed class FindWidget
{
	private const float Width = 340f;
	private const float Height = 32f;
	private const float FieldHeight = 22f;
	private const float ButtonSize = 20f;
	private const float PaddingX = 8f;

	private const float Offset = 8f;

	private readonly StringBuilder _query = new();

	private int _matchCount;
	private int _matchRank;

	private int _countedVersion = -1;
	private string _countedQuery = string.Empty;

	private float _left;
	private float _top;
	private int _hoverButton = -1;

	public bool IsOpen { get; private set; }

	public bool MatchCase { get; private set; }

	public ReadOnlySpan<char> Query => _query.Length == 0 ? default : _query.ToString();

	public void Show(TextView? view) {
		IsOpen = true;

		if (view is { HasSelection: true } selected) {
			string text = selected.GetSelectedText();

			if (!text.Contains('\n')) {
				_query.Clear();
				_query.Append(text);
				Invalidate();
			}
		}
	}

	public void Hide() => IsOpen = false;

	public void Append(char character) {
		_query.Append(character);
		Invalidate();
	}

	public void Backspace() {
		if (_query.Length > 0) {
			_query.Length--;
			Invalidate();
		}
	}

	public void ToggleMatchCase() {
		MatchCase = !MatchCase;
		Invalidate();
	}

	private void Invalidate() => _countedVersion = -1;

	public bool Restart(TextView? view) {
		if (view is null || _query.Length == 0) return false;

		string query = _query.ToString();
		int hit = TextSearch.Next(view.Snapshot, query, view.Selection.Start, MatchCase);

		if (hit < 0) return false;

		view.MoveTo(hit, extend: false);
		view.MoveTo(hit + query.Length, extend: true);
		return true;
	}

	public bool Step(TextView? view, int direction) {
		if (view is null || _query.Length == 0) return false;

		string query = _query.ToString();

		int from = direction > 0
			? Math.Min(view.Selection.Start + 1, view.Snapshot.Length)
			: view.Selection.Start;

		int hit = direction > 0
			? TextSearch.Next(view.Snapshot, query, from, MatchCase)
			: TextSearch.Previous(view.Snapshot, query, from, MatchCase);

		if (hit < 0) return false;

		view.MoveTo(hit, extend: false);
		view.MoveTo(hit + query.Length, extend: true);
		return true;
	}

	private void Recount(TextView? view) {
		if (view is null) {
			_matchCount = _matchRank = 0;
			return;
		}

		string query = _query.ToString();

		if (_countedVersion == view.Snapshot.Version && _countedQuery == query) {
			_matchRank = _matchCount == 0
				? 0
				: TextSearch.RankOf(view.Snapshot, query, view.Selection.Start, MatchCase);
			return;
		}

		_countedVersion = view.Snapshot.Version;
		_countedQuery = query;

		_matchCount = TextSearch.Count(view.Snapshot, query, MatchCase);
		_matchRank = _matchCount == 0
			? 0
			: TextSearch.RankOf(view.Snapshot, query, view.Selection.Start, MatchCase);
	}

	#region Hit testing

	private const int ButtonCount = 4;

	public bool Contains(float x, float y, float scale) =>
		IsOpen && x >= _left && x < _left + (Width * scale) && y >= _top && y < _top + (Height * scale);

	public int ButtonAt(float x, float y, float scale) {
		if (!Contains(x, y, scale)) return -1;

		float size = ButtonSize * scale;
		float gap = Metrics.Space2 * scale;
		float right = _left + (Width * scale) - (PaddingX * scale);
		float first = right - (ButtonCount * size) - ((ButtonCount - 1) * gap);

		if (x < first) return -1;

		int index = (int)((x - first) / (size + gap));
		return index >= 0 && index < ButtonCount ? index : -1;
	}

	public void SetHover(int button) => _hoverButton = button;

	#endregion

	public bool Press(int button, TextView? view) {
		switch (button) {
			case 0:
				ToggleMatchCase();
				return true;

			case 1:
				Step(view, -1);
				return true;

			case 2:
				Step(view, 1);
				return true;

			case 3:
				Hide();
				return true;

			default:
				return false;
		}
	}

	public void Render(IPainter painter, TextView? view, float editorRight, float editorTop, float scale) {
		if (!IsOpen) return;

		Recount(view);

		ITextFont font = painter.GetUi();

		float width = Width * scale;
		float height = Height * scale;
		float padding = PaddingX * scale;

		_left = editorRight - width - (Offset * scale);
		_top = editorTop + (Offset * scale);

		float radius = Metrics.RadiusMedium * scale;

		painter.FillRoundedRectangle(_left, _top, width, height, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(_left, _top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		float centreY = _top + (height / 2f);
		float baseline = centreY - (font.GetLineHeight() / 2f) + font.GetBaseline();

		float size = ButtonSize * scale;
		float gap = Metrics.Space2 * scale;
		float buttonsWidth = (ButtonCount * size) + ((ButtonCount - 1) * gap);
		float buttonsLeft = _left + width - padding - buttonsWidth;

		float fieldLeft = _left + padding;
		float fieldWidth = Math.Max(0f, buttonsLeft - fieldLeft - (Metrics.Space8 * scale));
		float fieldHeight = FieldHeight * scale;
		float fieldTop = centreY - (fieldHeight / 2f);

		bool noResults = _query.Length > 0 && _matchCount == 0;

		painter.FillRoundedRectangle(fieldLeft, fieldTop, fieldWidth, fieldHeight,
			Metrics.RadiusSmall * scale, Theme.EditorBackground);

		painter.StrokeRoundedRectangle(fieldLeft, fieldTop, fieldWidth, fieldHeight,
			Metrics.RadiusSmall * scale, Metrics.StrokeThickness * scale,
			noResults ? Color.Parse("#BE1100") : Theme.WidgetBorder);

		ReadOnlySpan<char> count = _query.Length == 0
			? default
			: _matchCount == 0 ? "No results" : $"{_matchRank} of {_matchCount}";

		float countWidth = count.IsEmpty ? 0f : font.Measure(count) + (Metrics.Space8 * scale);

		if (!count.IsEmpty) {
			painter.DrawText(font, count, fieldLeft + fieldWidth - countWidth + (Metrics.Space4 * scale),
				baseline, Theme.SectionHeaderForeground);
		}

		float textLeft = fieldLeft + (Metrics.Space6 * scale);

		painter.PushClip(fieldLeft, fieldTop, Math.Max(0f, fieldWidth - countWidth), fieldHeight);

		if (_query.Length == 0) painter.DrawText(font, "Find", textLeft, baseline, Theme.SectionHeaderForeground);
		else {
			Span<char> buffer = stackalloc char[128];
			int length = Math.Min(_query.Length, buffer.Length);

			for (int i = 0; i < length; i++) buffer[i] = _query[i];

			painter.DrawText(font, buffer[..length], textLeft, baseline, Theme.EditorForeground);

			painter.FillRectangle(textLeft + font.Measure(buffer[..length]), fieldTop + (4f * scale),
				Metrics.StrokeThickness * scale * 2f, fieldHeight - (8f * scale), Theme.CursorForeground);
		}

		painter.PopClip();

		DrawButton(painter, 0, Codicon.CaseSensitive, buttonsLeft, centreY, size, gap, scale, MatchCase);
		DrawButton(painter, 1, Codicon.ChevronUp, buttonsLeft, centreY, size, gap, scale, false);
		DrawButton(painter, 2, Codicon.ChevronDown, buttonsLeft, centreY, size, gap, scale, false);
		DrawButton(painter, 3, Codicon.Close, buttonsLeft, centreY, size, gap, scale, false);
	}

	private void DrawButton(IPainter painter, int index, Codicon icon, float buttonsLeft, float centreY,
		float size, float gap, float scale, bool active) {
		float left = buttonsLeft + (index * (size + gap));

		if (active || _hoverButton == index) {
			painter.FillRoundedRectangle(left, centreY - (size / 2f), size, size,
				Metrics.RadiusSmall * scale,
				active ? Theme.ListActiveSelectionBackground : Theme.ListHoverBackground);
		}

		painter.DrawIcon(icon, left + (size / 2f), centreY, Metrics.IconSize * scale,
			Theme.EditorForeground);
	}
}
