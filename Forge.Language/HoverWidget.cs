using Forge.Common.Render;
using Forge.Editor;
using Forge.Theming;

namespace Forge.Language;

public sealed class HoverWidget
{
	public const long DelayMilliseconds = 300;

	private const float Padding = 8f;
	private const float MaxWidth = 500f;

	private readonly List<(string Text, bool IsCode)> _lines = [];

	private float _x;
	private float _y;

	public int Line { get; private set; }

	public int Column { get; private set; }

	public bool IsOpen { get; private set; }

	public void Aim(int line, int column, float x, float y) {
		Line = line;
		Column = column;
		_x = x;
		_y = y;
	}

	public void Show(string markdown, int line, int column) {
		if (line != Line || column != Column || markdown.Length == 0) return;

		_lines.Clear();
		bool inFence = false;

		foreach (string raw in markdown.Split('\n')) {
			string text = raw.TrimEnd('\r');

			if (text.TrimStart().StartsWith("```", StringComparison.Ordinal)) {
				inFence = !inFence;
				continue;
			}

			if (text.Trim().Length == 0 && (_lines.Count == 0 || _lines[^1].Text.Length == 0)) continue;

			_lines.Add((text, inFence));
		}

		IsOpen = _lines.Count > 0;
	}

	public void Hide() {
		IsOpen = false;
		_lines.Clear();
	}

	public void Render(IPainter painter, float editorTop, float editorBottom, float editorRight,
		float scale) {
		if (!IsOpen) return;

		ITextFont code = painter.GetCode();
		ITextFont ui = painter.GetUi();

		float padding = Padding * scale;
		float limit = MaxWidth * scale;

		List<(string Text, bool IsCode)> wrapped = [];
		float widest = 0f;

		foreach ((string text, bool isCode) in _lines) {
			ITextFont font = isCode ? code : ui;

			foreach (string line in Wrap(font, text, limit - (padding * 2f))) {
				wrapped.Add((line, isCode));
				widest = Math.Max(widest, font.Measure(line));
			}
		}

		float height = padding * 2f;

		foreach ((_, bool isCode) in wrapped) {
			height += (isCode ? code : ui).GetLineHeight();
		}

		float width = Math.Min(limit, widest + (padding * 2f));

		float top = _y + (16f * scale);

		if (top + height > editorBottom) top = Math.Max(editorTop, _y - height - (4f * scale));

		float left = Math.Max(0f, Math.Min(_x, editorRight - width));
		float radius = Metrics.RadiusMedium * scale;

		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(left, top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		painter.PushClip(left, top, width, height);

		float y = top + padding;

		foreach ((string text, bool isCode) in wrapped) {
			ITextFont font = isCode ? code : ui;

			painter.DrawText(font, text, left + padding, y + font.GetBaseline(),
				isCode ? Theme.EditorForeground : Theme.SectionHeaderForeground);

			y += font.GetLineHeight();
		}

		painter.PopClip();
	}

	private static List<string> Wrap(ITextFont font, string text, float width) {
		List<string> lines = [];

		if (text.Length == 0 || font.Measure(text) <= width) {
			lines.Add(text);
			return lines;
		}

		int start = 0;
		int lastSpace = -1;

		for (int i = 0; i < text.Length; i++) {
			if (text[i] == ' ') lastSpace = i;

			if (font.Measure(text.AsSpan(start, i - start + 1)) <= width) continue;

			int end = lastSpace > start ? lastSpace : i;

			lines.Add(text[start..end]);
			start = lastSpace > start ? end + 1 : end;
			lastSpace = -1;
		}

		if (start < text.Length) lines.Add(text[start..]);

		return lines;
	}
}
