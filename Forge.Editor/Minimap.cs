using Forge.Common.Render;
using Forge.Syntax;
using Forge.Text;
using Forge.Theming;

namespace Forge.Editor;

public sealed class Minimap
{
	private const float RowHeight = 2f;
	private const float CharWidth = 1f;
	private const int MaxColumns = 76;

	private char[] _raw = new char[512];

	public static float Width(float scale) => Metrics.MinimapWidth * scale;

	public void Render(IPainter painter, TextView view, Highlighter highlighter, float x, float y, float width,
		float height, float scale) {
		TextSnapshot snapshot = view.Snapshot;

		painter.FillRectangle(x, y, width, height, Theme.EditorBackground);
		painter.PushClip(x, y, width, height);

		float rowHeight = RowHeight * scale;
		float charWidth = CharWidth * scale;

		int rows = Math.Max(1, (int)(height / rowHeight));

		int totalLines = snapshot.LineCount;
		int firstLine = 0;

		if (totalLines > rows) {
			int caretLine = view.CaretLine;
			firstLine = Math.Clamp(caretLine - (rows / 2), 0, Math.Max(0, totalLines - rows));
		}

		int lastLine = Math.Min(totalLines - 1, firstLine + rows);

		for (int line = firstLine; line <= lastLine; line++) {
			float rowTop = y + ((line - firstLine) * rowHeight);
			DrawLine(painter, snapshot, highlighter, line, x + (Metrics.SpaceXs * scale), rowTop, charWidth,
				rowHeight);
		}

		DrawViewportSlider(painter, view, x, y, width, height, rowHeight, firstLine, rows, totalLines);

		painter.PopClip();
	}

	private void DrawLine(IPainter painter, TextSnapshot snapshot, Highlighter highlighter, int line, float x,
		float rowTop, float charWidth, float rowHeight) {
		TextSpan span = snapshot.GetLineSpan(line);
		if (span.Length == 0) return;

		int length = Math.Min(span.Length, MaxColumns);

		if (!highlighter.IsEnabled) {
			EnsureCapacity(ref _raw, length);
			snapshot.CopyTo(span.Start, length, _raw);

			int indent = CountIndent(_raw, length);
			if (indent < length) {
				painter.FillRectangle(x + (indent * charWidth), rowTop, (length - indent) * charWidth,
					rowHeight - 1f, Theme.MinimapSlider);
			}

			return;
		}

		List<Token> tokens = highlighter.TokenizeLine(snapshot, line);
		for (int i = 0; i < tokens.Count; i++) {
			Token token = tokens[i];
			if (token.Start >= MaxColumns) break;

			int end = Math.Min(token.End, MaxColumns);
			float barWidth = (end - token.Start) * charWidth;
			if (barWidth <= 0f) continue;

			Color color = SyntaxColors.For(token.Kind);
			painter.FillRectangle(x + (token.Start * charWidth), rowTop, barWidth, rowHeight - 1f,
				color.WithAlpha(0.75f));
		}
	}

	private static void DrawViewportSlider(IPainter painter, TextView view, float x, float y, float width,
		float height, float rowHeight, int firstLine, int rows, int totalLines) {
		float lineHeight = Math.Max(1f, view.LastLineHeight);
		int visibleLines = Math.Max(1, (int)(view.LastViewportHeight / lineHeight));

		int viewportFirst = view.LineAtY(view.ScrollY, lineHeight);

		float sliderTop = y + ((viewportFirst - firstLine) * rowHeight);
		float sliderHeight = visibleLines * rowHeight;

		if (sliderTop + sliderHeight < y || sliderTop > y + height) return;

		painter.FillRectangle(x, sliderTop, width, sliderHeight, Theme.MinimapSlider);
	}

	private static int CountIndent(char[] text, int length) {
		int i = 0;
		while (i < length && (text[i] == ' ' || text[i] == '\t')) i++;

		return i;
	}

	private static void EnsureCapacity(ref char[] buffer, int required) {
		if (required <= buffer.Length) return;

		int capacity = buffer.Length;
		while (capacity < required) capacity *= 2;

		buffer = new char[capacity];
	}
}
