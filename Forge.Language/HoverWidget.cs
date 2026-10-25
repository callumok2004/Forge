using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Language;

public sealed class HoverWidget : Panel
{
	public const long DelayMilliseconds = 300;

	private const float Padding = 8f;
	private const float MaxWidth = 500f;
	private const float PointerOffset = 16f;

	private readonly List<(string Text, bool IsCode)> _lines = [];
	private readonly List<(string Text, bool IsCode)> _wrapped = [];

	private float _x;
	private float _y;
	private bool _rewrap;

	public HoverWidget() {
		Name = "Hover";
		Visible = false;
		MouseInputEnabled = false;
	}

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

		lock (_lines) {
			_lines.Clear();
			bool inFence = false;

			foreach (string raw in markdown.Split('\n')) {
				string text = raw.TrimEnd('\r');

				if (text.TrimStart().StartsWith("```", StringComparison.Ordinal)) {
					inFence = !inFence;
					continue;
				}

				if (text.Trim().Length == 0 && (_lines.Count == 0 || _lines[^1].Text.Length == 0)) continue;

				_lines.Add((inFence ? text : Inline(text), inFence));
			}

			_rewrap = true;
			IsOpen = _lines.Count > 0;
		}
	}

	private static readonly System.Buffers.SearchValues<char> InlineMarkers = System.Buffers.SearchValues.Create("`\\*_[&");

	public static string Inline(string text) {
		if (!text.AsSpan().ContainsAny(InlineMarkers)) return text;

		char[]? rented = text.Length > 512 ? System.Buffers.ArrayPool<char>.Shared.Rent(text.Length) : null;
		Span<char> buffer = rented ?? stackalloc char[512];
		string result = new(buffer[..Inline(text, buffer)]);

		if (rented is not null) System.Buffers.ArrayPool<char>.Shared.Return(rented);

		return result;
	}

	private static int Inline(ReadOnlySpan<char> text, Span<char> destination) {
		int written = 0;
		bool inCode = false;

		for (int i = 0; i < text.Length; i++) {
			char c = text[i];

			if (c == '`') {
				inCode = !inCode;
				continue;
			}

			if (inCode) {
				destination[written++] = c;
				continue;
			}

			if (c == '\\' && i + 1 < text.Length && !char.IsAsciiLetterOrDigit(text[i + 1]) && text[i + 1] is > ' ' and < '\x7f') {
				destination[written++] = text[++i];
				continue;
			}

			if (c is '*' or '_' && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1]) || i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1]))) continue;

			if (c == '[' && text[i..].IndexOf("](") is int close and > 0 && text[(i + close)..].IndexOf(')') is int end and > 0) {
				written += Inline(text.Slice(i + 1, close - 1), destination[written..]);
				i += close + end;
				continue;
			}

			if (c == '&' && text[i..].StartsWith("&nbsp;")) {
				destination[written++] = ' ';
				i += 5;
				continue;
			}

			destination[written++] = c;
		}

		return written;
	}

	public void Hide() {
		IsOpen = false;
	}

	public void Place(float areaTop, float areaBottom, float areaRight, float originX, float originY) {
		Visible = IsOpen;

		if (!IsOpen || Ui is null) return;

		float scale = Scale;
		IFontSet fonts = Ui.GetFonts();
		ITextFont code = fonts.GetCode();
		ITextFont ui = fonts.GetUi();
		float padding = Padding * scale;
		float limit = MaxWidth * scale;

		if (_rewrap) {
			_rewrap = false;
			_wrapped.Clear();

			lock (_lines) {
				List<string> pieces = [];

				foreach ((string text, bool isCode) in _lines) {
					pieces.Clear();
					SuggestDetails.Wrap(isCode ? code : ui, text, limit - (padding * 2f), pieces);

					foreach (string piece in pieces) _wrapped.Add((piece, isCode));
				}
			}
		}

		float widest = 0f;
		float height = padding * 2f;

		foreach ((string text, bool isCode) in _wrapped) {
			ITextFont font = isCode ? code : ui;
			widest = Math.Max(widest, font.Measure(text));
			height += font.GetLineHeight();
		}

		float width = Math.Min(limit, widest + (padding * 2f));
		float top = _y + (PointerOffset * scale);

		if (top + height > areaBottom) top = Math.Max(areaTop, _y - height - (4f * scale));

		float left = Math.Max(0f, Math.Min(_x, areaRight - width));
		SetBounds(left - originX, top - originY, width, height);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont code = painter.GetCode();
		ITextFont ui = painter.GetUi();
		float padding = Padding * scale;
		float radius = Metrics.RadiusMedium * scale;
		float y = ScreenY + padding;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		foreach ((string text, bool isCode) in _wrapped) {
			ITextFont font = isCode ? code : ui;
			painter.DrawText(font, text, ScreenX + padding, y + font.GetBaseline(), isCode ? Theme.EditorForeground : Theme.SectionHeaderForeground);
			y += font.GetLineHeight();
		}

		painter.PopClip();
	}
}
