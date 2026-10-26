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

	private const float CellGap = 12f;

	public enum LineKind : byte
	{
		Text,
		Code,
		Rule,
		Row,
		HeaderRow,
	}

	public readonly record struct HoverLine(string Text, LineKind Kind, string[]? Cells = null, int Table = -1);

	private readonly List<HoverLine> _lines = [];
	private readonly List<HoverLine> _wrapped = [];
	private readonly List<float[]> _tableColumns = [];

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
			Parse(markdown, _lines);
			_rewrap = true;
			IsOpen = _lines.Count > 0;
		}
	}

	public static void Parse(string markdown, List<HoverLine> into) {
		into.Clear();
		bool inFence = false;
		int table = -1;
		bool previousRow = false;
		ReadOnlySpan<char> rest = markdown;

		while (!rest.IsEmpty) {
			int newline = rest.IndexOf('\n');
			ReadOnlySpan<char> raw = newline < 0 ? rest : rest[..newline];
			rest = newline < 0 ? [] : rest[(newline + 1)..];
			ReadOnlySpan<char> text = raw.TrimEnd('\r');
			ReadOnlySpan<char> trimmed = text.Trim();

			if (trimmed.StartsWith("```")) {
				inFence = !inFence;
				previousRow = false;
				continue;
			}

			if (inFence) {
				into.Add(new HoverLine(text.ToString(), LineKind.Code));
				continue;
			}

			if (trimmed.Length > 1 && trimmed[0] == '|') {
				if (IsDelimiterRow(trimmed)) {
					if (previousRow && into[^1].Kind == LineKind.Row) into[^1] = into[^1] with { Kind = LineKind.HeaderRow };

					continue;
				}

				if (!previousRow) table++;

				into.Add(new HoverLine(string.Empty, LineKind.Row, Cells(trimmed), table));
				previousRow = true;
				continue;
			}

			previousRow = false;

			if (trimmed.Length >= 3 && trimmed.IndexOfAnyExcept('-') < 0) {
				if (into.Count > 0 && into[^1].Kind != LineKind.Rule) into.Add(new HoverLine(string.Empty, LineKind.Rule));

				continue;
			}

			if (trimmed.IsEmpty && (into.Count == 0 || into[^1] is { Kind: LineKind.Text, Text.Length: 0 } or { Kind: LineKind.Rule })) continue;

			into.Add(new HoverLine(Inline(text.ToString()), LineKind.Text));
		}

		while (into.Count > 0 && into[^1] is { Kind: LineKind.Rule } or { Kind: LineKind.Text, Text.Length: 0 }) into.RemoveAt(into.Count - 1);
	}

	private static bool IsDelimiterRow(ReadOnlySpan<char> row) => row.IndexOfAnyExcept("|-: \t") < 0 && row.Contains('-');

	private static string[] Cells(ReadOnlySpan<char> row) {
		row = row[1..];

		if (row.EndsWith("|") && !row.EndsWith("\\|")) row = row[..^1];

		List<string> cells = [];
		Span<char> buffer = row.Length <= 512 ? stackalloc char[row.Length] : new char[row.Length];
		int written = 0;

		for (int i = 0; i <= row.Length; i++) {
			if (i < row.Length && row[i] == '\\' && i + 1 < row.Length && row[i + 1] == '|') {
				buffer[written++] = '|';
				i++;
				continue;
			}

			if (i < row.Length && row[i] != '|') {
				buffer[written++] = row[i];
				continue;
			}

			cells.Add(Inline(buffer[..written].Trim().ToString()));
			written = 0;
		}

		return [.. cells];
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
			_tableColumns.Clear();

			lock (_lines) {
				List<string> pieces = [];

				foreach (HoverLine line in _lines) {
					if (line.Kind is LineKind.Row or LineKind.HeaderRow) {
						MeasureRow(ui, line);
						_wrapped.Add(line);
						continue;
					}

					if (line.Kind == LineKind.Rule) {
						_wrapped.Add(line);
						continue;
					}

					pieces.Clear();
					SuggestDetails.Wrap(line.Kind == LineKind.Code ? code : ui, line.Text, limit - (padding * 2f), pieces);

					foreach (string piece in pieces) _wrapped.Add(line with { Text = piece });
				}
			}
		}

		float widest = 0f;
		float height = padding * 2f;
		float gap = CellGap * scale;

		foreach (HoverLine line in _wrapped) {
			switch (line.Kind) {
				case LineKind.Rule:
					height += RuleHeight(scale);
					break;
				case LineKind.Row or LineKind.HeaderRow:
					float rowWidth = 0f;

					foreach (float column in _tableColumns[line.Table]) rowWidth += column + gap;

					widest = Math.Max(widest, rowWidth - gap);
					height += ui.GetLineHeight();
					break;
				default:
					ITextFont font = line.Kind == LineKind.Code ? code : ui;
					widest = Math.Max(widest, font.Measure(line.Text));
					height += font.GetLineHeight();
					break;
			}
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

		float gap = CellGap * scale;

		foreach (HoverLine line in _wrapped) {
			switch (line.Kind) {
				case LineKind.Rule:
					float rule = RuleHeight(scale);
					painter.FillRectangle(ScreenX, y + MathF.Floor(rule / 2f), Wide, MathF.Max(1f, scale), Theme.WidgetBorder);
					y += rule;
					break;
				case LineKind.Row or LineKind.HeaderRow:
					float x = ScreenX + padding;
					float[] columns = _tableColumns[line.Table];
					string[] cells = line.Cells!;

					for (int i = 0; i < cells.Length && i < columns.Length; i++) {
						painter.DrawText(ui, cells[i], x, y + ui.GetBaseline(), line.Kind == LineKind.HeaderRow ? Theme.EditorForeground : Theme.SectionHeaderForeground);
						x += columns[i] + gap;
					}

					if (line.Kind == LineKind.HeaderRow) painter.FillRectangle(ScreenX + padding, y + ui.GetLineHeight() - MathF.Max(1f, scale), Wide - (padding * 2f), MathF.Max(1f, scale), Theme.WidgetBorder);

					y += ui.GetLineHeight();
					break;
				default:
					ITextFont font = line.Kind == LineKind.Code ? code : ui;
					painter.DrawText(font, line.Text, ScreenX + padding, y + font.GetBaseline(), line.Kind == LineKind.Code ? Theme.EditorForeground : Theme.SectionHeaderForeground);
					y += font.GetLineHeight();
					break;
			}
		}

		painter.PopClip();
	}

	private static float RuleHeight(float scale) => MathF.Round(9f * scale);

	private void MeasureRow(ITextFont font, HoverLine line) {
		while (_tableColumns.Count <= line.Table) _tableColumns.Add([]);

		float[] columns = _tableColumns[line.Table];

		if (columns.Length < line.Cells!.Length) {
			Array.Resize(ref columns, line.Cells.Length);
			_tableColumns[line.Table] = columns;
		}

		for (int i = 0; i < line.Cells.Length; i++) columns[i] = MathF.Max(columns[i], font.Measure(line.Cells[i]));
	}
}
