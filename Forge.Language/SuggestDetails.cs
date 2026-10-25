using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Language;

public sealed class SuggestDetails : Panel
{
	private const float PaddingX = 5f;
	private const float PaddingY = 4f;
	private const float DetailGap = 12f;

	private readonly SuggestWidget _suggest;
	private readonly List<string> _lines = [];

	private string _wrappedFor = string.Empty;
	private float _wrappedWidth;

	public SuggestDetails(SuggestWidget suggest) {
		_suggest = suggest;
		Name = "SuggestDetails";
		Visible = false;
		MouseInputEnabled = false;
	}

	public void Place(float listLeft, float listTop, float listWidth, float areaTop, float areaBottom, float areaRight, float originX, float originY) {
		Visible = _suggest.DetailsVisible && (_suggest.Detail.Length > 0 || _suggest.Documentation.Length > 0);

		if (!Visible || Ui is null) return;

		float scale = Scale;
		IFontSet fonts = Ui.GetFonts();
		ITextFont font = fonts.GetUi();
		ITextFont type = fonts.GetCodeSmall();
		float width = listWidth;
		float left = listLeft + listWidth;

		if (left + width > areaRight) left = Math.Max(0f, listLeft - width);

		Rewrap(font, width - (PaddingX * 2f * scale));

		float height = PaddingY * 2f * scale;

		if (_suggest.Detail.Length > 0) height += type.GetLineHeight() + (_suggest.Documentation.Length > 0 ? DetailGap * scale : 0f);

		int shown = Math.Min(_lines.Count, Math.Max(1, (int)((areaBottom - listTop - height) / font.GetLineHeight())));
		height += shown * font.GetLineHeight();

		float top = Math.Max(areaTop, Math.Min(listTop, areaBottom - height));
		SetBounds(left - originX, top - originY, width, height);
	}

	private void Rewrap(ITextFont font, float width) {
		if (ReferenceEquals(_wrappedFor, _suggest.Documentation) && _wrappedWidth == width) return;

		_wrappedFor = _suggest.Documentation;
		_wrappedWidth = width;
		_lines.Clear();

		foreach (string paragraph in _suggest.Documentation.Split('\n')) Wrap(font, paragraph.TrimEnd('\r'), width, _lines);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi();
		ITextFont type = painter.GetCodeSmall();
		float radius = Metrics.RadiusLarge * scale;
		float y = ScreenY + (PaddingY * scale);
		float left = ScreenX + (PaddingX * scale);

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
		painter.PushRoundedClip(ScreenX, ScreenY, Wide, Tall, radius);

		if (_suggest.Detail.Length > 0) {
			painter.DrawText(type, _suggest.Detail.ReplaceLineEndings(string.Empty), left, y + type.GetBaseline(), Theme.SuggestForeground.WithAlpha(Theme.SuggestForeground.A * 0.7f));
			y += type.GetLineHeight() + (_suggest.Documentation.Length > 0 ? DetailGap * scale : 0f);
		}

		foreach (string line in _lines) {
			if (y >= ScreenY + Tall) break;

			painter.DrawText(font, line, left, y + font.GetBaseline(), Theme.SuggestForeground);
			y += font.GetLineHeight();
		}

		painter.PopRoundedClip();
	}

	public static void Wrap(ITextFont font, string text, float width, List<string> into) {
		if (text.Length == 0 || font.Measure(text) <= width) {
			into.Add(text);
			return;
		}

		int start = 0;
		int lastSpace = -1;

		for (int i = 0; i < text.Length; i++) {
			if (text[i] == ' ') lastSpace = i;

			if (font.Measure(text.AsSpan(start, i - start + 1)) <= width) continue;

			int end = lastSpace > start ? lastSpace : i;
			into.Add(text[start..end]);
			start = lastSpace > start ? end + 1 : end;
			lastSpace = -1;
		}

		if (start < text.Length) into.Add(text[start..]);
	}
}
