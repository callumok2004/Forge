using Forge.Common.Render;
using Forge.Syntax;
using Forge.Text;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Language;

public sealed class HoverMarkdownLayout
{
	private const float FontSize = 13f;
	private const float LineHeightEm = 1.5f;
	private const float HeadingLineHeight = 1.1f;
	private const float BlockMargin = 8f;
	private const float RuleMarginTop = 4f;
	private const float RuleMarginBottom = -4f;
	private const float ListIndent = 20f;
	private const float CodePaddingEm = 0.4f;
	private const float CodeRadius = 3f;
	private const float CellPadding = 1f;
	private const float CellSpacing = 2f;
	private const float IconMarginBottom = 2f;

	public readonly record struct LinkBox(float X, float Y, float Width, float Height, string Href);

	public readonly record struct IconDraw(float X, float Y, float Size, Codicon Icon, Color Color, string? Href);

	public readonly record struct ImageDraw(float X, float Y, float Width, float Height, IImage Image);

	public readonly record struct BoxDraw(float X, float Y, float Width, float Height, float Radius, Color Color);

	public readonly record struct LinkText(float X, float Y, string Text, ITextFont Font, string Href);

	public readonly record struct RuleDraw(float Y);

	public List<LinkBox> Links { get; } = [];

	public List<IconDraw> Icons { get; } = [];

	public List<ImageDraw> Images { get; } = [];

	public List<BoxDraw> Boxes { get; } = [];

	public List<LinkText> LinkTexts { get; } = [];

	public List<RuleDraw> Rules { get; } = [];

	public static Func<string, IImage?>? ImageLoader { get; set; }

	private static readonly Dictionary<string, IImage?> ImageCache = [with(StringComparer.OrdinalIgnoreCase)];

	private readonly IFontSet _fonts;
	private readonly float _scale;
	private readonly TextSelection _selection;
	private float _widest;

	public HoverMarkdownLayout(IFontSet fonts, float scale, TextSelection selection) {
		_fonts = fonts;
		_scale = scale;
		_selection = selection;
	}

	public float Widest => _widest;

	public void Clear() {
		Links.Clear();
		Icons.Clear();
		Images.Clear();
		Boxes.Clear();
		LinkTexts.Clear();
		Rules.Clear();
		_widest = 0f;
	}

	private float Line => MathF.Round(FontSize * LineHeightEm * _scale);

	private ITextFont FontFor(InlineStyle style, float size, bool baseBold) {
		bool bold = baseBold || (style & InlineStyle.Bold) != 0;

		if ((style & InlineStyle.Code) != 0) return _fonts.GetMono(size, bold);

		if ((style & InlineStyle.Italic) != 0 && !bold) return _fonts.GetUiItalic(size);

		return _fonts.GetUi(size, bold);
	}

	private static IImage? Image(string source) {
		if (ImageLoader is not { } loader) return null;

		string? path = null;

		if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.IsFile) path = uri.LocalPath;
		else if (Path.IsPathFullyQualified(source)) path = source;

		if (path is null || !File.Exists(path)) return null;

		lock (ImageCache) {
			if (ImageCache.TryGetValue(path, out IImage? cached)) return cached;

			IImage? loaded;

			try {
				loaded = loader(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException) {
				loaded = null;
			}

			ImageCache[path] = loaded;
			return loaded;
		}
	}

	private readonly record struct Atom(float Width, float Height, string? Text, ITextFont? Font, InlineStyle Style, int Link, Codicon? Icon, IImage? Image, bool Space, bool Break, int Span);

	private List<Atom> Atoms(IReadOnlyList<InlineSpan> spans, float size, bool baseBold, List<string> hrefs) {
		List<Atom> atoms = [];

		for (int s = 0; s < spans.Count; s++) {
			InlineSpan span = spans[s];
			int link = -1;

			if (span.Href is { } href) {
				link = hrefs.IndexOf(href);

				if (link < 0) {
					hrefs.Add(href);
					link = hrefs.Count - 1;
				}
			}

			if (span.IsBreak) {
				atoms.Add(new Atom(0, 0, null, null, span.Style, link, null, null, false, true, s));
				continue;
			}

			if (span.Icon is { } icon) {
				float iconSize = size * _scale;
				atoms.Add(new Atom(iconSize, iconSize, null, null, span.Style, link, icon, null, false, false, s));
				continue;
			}

			if (span.Image is { } source) {
				if (Image(source) is { } image) atoms.Add(new Atom(image.GetWidth() * _scale, image.GetHeight() * _scale, null, null, span.Style, link, null, image, false, false, s));
				else if (span.Text.Length > 0) AddWords(atoms, span.Text, FontFor(span.Style, size, baseBold), span.Style, link, s);

				continue;
			}

			AddWords(atoms, span.Text, FontFor(span.Style, size, baseBold), span.Style, link, s);
		}

		return atoms;
	}

	private void AddWords(List<Atom> atoms, string text, ITextFont font, InlineStyle style, int link, int span) {
		bool code = (style & InlineStyle.Code) != 0;
		float padding = code ? CodePaddingEm * font.GetSizeInPixels() : 0f;
		int start = 0;
		bool first = true;

		while (start < text.Length) {
			bool space = text[start] == ' ';
			int end = start;

			while (end < text.Length && (text[end] == ' ') == space) end++;

			string piece = text[start..end];
			bool last = end >= text.Length;
			float extra = (first ? padding : 0f) + (last ? padding : 0f);
			atoms.Add(new Atom(font.Measure(piece) + extra, font.GetLineHeight(), piece, font, style, link, null, null, space, false, span));
			first = false;
			start = end;
		}

		if (text.Length == 0 && code) atoms.Add(new Atom(padding * 2f, font.GetLineHeight(), string.Empty, font, style, link, null, null, false, false, span));
	}

	private float Flow(IReadOnlyList<InlineSpan> spans, float left, float width, float y, float size, float lineHeight, bool baseBold, Color color, CellAlignment alignment, List<string> hrefs, bool joinFirst = false) {
		List<Atom> atoms = Atoms(spans, size, baseBold, hrefs);
		List<List<Atom>> lines = [[]];
		float x = 0f;

		foreach (Atom atom in atoms) {
			if (atom.Break) {
				lines.Add([]);
				x = 0f;
				continue;
			}

			if (atom.Space) {
				if (lines[^1].Count == 0) continue;

				lines[^1].Add(atom);
				x += atom.Width;
				continue;
			}

			if (x + atom.Width > width && x > 0f) {
				while (lines[^1].Count > 0 && lines[^1][^1].Space) lines[^1].RemoveAt(lines[^1].Count - 1);

				lines.Add([]);
				x = 0f;
			}

			if (atom.Width > width && atom.Text is { Length: > 1 } text && atom.Font is { } font) {
				int from = 0;

				while (from < text.Length) {
					int count = 1;

					while (from + count < text.Length && font.Measure(text.AsSpan(from, count + 1)) <= width - x) count++;

					string slice = text.Substring(from, count);
					Atom part = atom with { Text = slice, Width = font.Measure(slice) };

					if (x + part.Width > width && x > 0f) {
						lines.Add([]);
						x = 0f;
					}

					lines[^1].Add(part);
					x += part.Width;
					from += count;
				}

				continue;
			}

			lines[^1].Add(atom);
			x += atom.Width;
		}

		bool firstRun = true;

		foreach (List<Atom> line in lines) {
			while (line.Count > 0 && line[^1].Space) line.RemoveAt(line.Count - 1);

			float tallest = lineHeight;

			foreach (Atom atom in line) {
				if (atom.Image is not null) tallest = MathF.Max(tallest, atom.Height);
			}

			float lineWidth = line.Sum(static atom => atom.Width);
			float cursor = alignment switch {
				CellAlignment.Center => left + MathF.Max(0f, (width - lineWidth) / 2f),
				CellAlignment.Right => left + MathF.Max(0f, width - lineWidth),
				_ => left,
			};
			ITextFont reference = FontFor(InlineStyle.None, size, baseBold);
			float baseline = y + (tallest - lineHeight) + ((lineHeight - reference.GetLineHeight()) / 2f) + reference.GetBaseline();

			for (int i = 0; i < line.Count; i++) {
				Atom atom = line[i];

				if (atom.Icon is { } icon) {
					Color iconColor = atom.Link >= 0 ? Theme.TextLinkForeground : color;
					float centre = y + (tallest - lineHeight) + (lineHeight / 2f) - (IconMarginBottom * _scale / 2f);
					Icons.Add(new IconDraw(cursor, centre - (atom.Width / 2f), atom.Width, icon, iconColor, atom.Link >= 0 ? hrefs[atom.Link] : null));

					if (atom.Link >= 0) Links.Add(new LinkBox(cursor, y, atom.Width, tallest, hrefs[atom.Link]));

					cursor += atom.Width;
					continue;
				}

				if (atom.Image is { } image) {
					Images.Add(new ImageDraw(cursor, baseline - atom.Height, atom.Width, atom.Height, image));

					if (atom.Link >= 0) Links.Add(new LinkBox(cursor, baseline - atom.Height, atom.Width, atom.Height, hrefs[atom.Link]));

					cursor += atom.Width;
					continue;
				}

				int end = i;
				string text = atom.Text ?? string.Empty;
				float runWidth = atom.Width;

				while (end + 1 < line.Count && line[end + 1] is { Icon: null, Image: null } next && next.Span == atom.Span && next.Font == atom.Font) {
					end++;
					text += next.Text;
					runWidth += next.Width;
				}

				ITextFont font = atom.Font!;
				bool code = (atom.Style & InlineStyle.Code) != 0;
				float padding = code ? CodePaddingEm * font.GetSizeInPixels() : 0f;
				bool startsSpan = i == 0 || line[i - 1].Span != atom.Span;
				float textX = cursor + (code && startsSpan ? padding : 0f);
				float textTop = baseline - font.GetBaseline();

				if (code) Boxes.Add(new BoxDraw(cursor, textTop, runWidth, font.GetLineHeight(), CodeRadius * _scale, Theme.TextCodeBlockBackground));

				string shown = text;
				Color runColor = atom.Link >= 0 ? Theme.TextLinkForeground : color;

				if (atom.Link >= 0) {
					_selection.Add(new TextRun(textX, textTop, shown, font, runColor.WithAlpha(0f), !firstRun || joinFirst));
					LinkTexts.Add(new LinkText(textX, textTop, shown, font, hrefs[atom.Link]));
					Links.Add(new LinkBox(cursor, y, runWidth, tallest, hrefs[atom.Link]));
				}
				else _selection.Add(new TextRun(textX, textTop, shown, font, runColor, !firstRun || joinFirst));

				firstRun = false;
				cursor += runWidth;
				i = end;
			}

			_widest = MathF.Max(_widest, cursor);
			y += tallest;
		}

		return y;
	}

	private readonly record struct Margins(float Top, float Bottom);

	private static Margins MarginsFor(MarkdownBlock block) => block.Kind switch {
		BlockKind.Rule => new Margins(RuleMarginTop, RuleMarginBottom),
		BlockKind.Table => new Margins(0f, 0f),
		_ => new Margins(BlockMargin, BlockMargin),
	};

	private static float Collapse(float a, float b) {
		if (a >= 0f && b >= 0f) return MathF.Max(a, b);

		if (a < 0f && b < 0f) return MathF.Min(a, b);

		return a + b;
	}

	public float Layout(IReadOnlyList<MarkdownBlock> blocks, float left, float width, float y, float ruleLeft, List<string> hrefs) {
		float pending = 0f;
		bool first = true;

		for (int index = 0; index < blocks.Count; index++) {
			MarkdownBlock block = blocks[index];
			bool listContinues = block.Kind == BlockKind.ListItem && index > 0 && blocks[index - 1].Kind == BlockKind.ListItem;
			Margins margins = MarginsFor(block);
			bool firstChildRule = block.Kind is BlockKind.Paragraph or BlockKind.Code or BlockKind.ListItem;
			float top = first && firstChildRule ? 0f : margins.Top;

			if (!listContinues) y += (first ? top : Collapse(pending, top)) * _scale;

			first = false;

			switch (block.Kind) {
				case BlockKind.Paragraph:
					y = Flow(block.Spans, left, width, y, FontSize, Line, false, Theme.EditorHoverForeground, CellAlignment.Left, hrefs);
					break;
				case BlockKind.Heading:
					float em = block.Level switch { 1 => 2f, 2 => 1.5f, 3 => 1.17f, 4 => 1f, 5 => 0.83f, _ => 0.67f };
					y = Flow(block.Spans, left, width, y, FontSize * em, MathF.Round(FontSize * em * HeadingLineHeight * _scale), true, Theme.EditorHoverForeground, CellAlignment.Left, hrefs);
					break;
				case BlockKind.ListItem:
					float indent = (block.Level + 1) * ListIndent * _scale;
					ITextFont markerFont = _fonts.GetUi(FontSize);
					float markerWidth = markerFont.Measure(block.Marker);
					_selection.Add(new TextRun(left + indent - markerWidth - (FontSize * 0.5f * _scale), y + ((Line - markerFont.GetLineHeight()) / 2f), block.Marker, markerFont, Theme.EditorHoverForeground, false, false));
					y = Flow(block.Spans, left + indent, width - indent, y, FontSize, Line, false, Theme.EditorHoverForeground, CellAlignment.Left, hrefs);
					break;
				case BlockKind.Rule:
					Rules.Add(new RuleDraw(y));
					y += MathF.Max(1f, _scale);
					break;
				case BlockKind.Code:
					y = Code(block, left, width, y);
					break;
				case BlockKind.Table:
					y = Table(block, left, width, y, hrefs);
					break;
			}

			bool nextContinuesList = block.Kind == BlockKind.ListItem && index + 1 < blocks.Count && blocks[index + 1].Kind == BlockKind.ListItem;

			if (!nextContinuesList) pending = margins.Bottom;
		}

		bool lastChildRule = blocks.Count > 0 && blocks[^1].Kind is BlockKind.Paragraph or BlockKind.Code or BlockKind.ListItem;

		if (blocks.Count > 0 && !lastChildRule) y += pending * _scale;

		return y;
	}

	private float Code(MarkdownBlock block, float left, float width, float y) {
		ITextFont font = _fonts.GetMono(FontSize);
		Highlighter? highlighter = block.Language is { Length: > 0 } language && Languages.ForGrammarId(language) is { } definition ? new Highlighter(definition) : null;
		TextSnapshot? snapshot = highlighter is not null ? new TextBuffer(string.Join("\n", block.Lines)).CreateSnapshot() : null;
		float line = Line;

		for (int index = 0; index < block.Lines.Count; index++) {
			string text = block.Lines[index].Replace("\t", "    ");
			List<Highlighter.ColoredToken>? tokens = highlighter is not null ? highlighter.ColorLine(snapshot!, index) : null;
			float x = left;
			float top = y + ((line - font.GetLineHeight()) / 2f);
			bool joined = false;
			int column = 0;

			void Emit(string piece, Color color) {
				int start = 0;

				while (start < piece.Length) {
					int count = piece.Length - start;

					while (count > 1 && x + font.Measure(piece.AsSpan(start, count)) > left + width && x > left) count--;

					if (x + font.Measure(piece.AsSpan(start, count)) > left + width && x > left) {
						y += line;
						top = y + ((line - font.GetLineHeight()) / 2f);
						x = left;
						continue;
					}

					string slice = piece.Substring(start, count);
					_selection.Add(new TextRun(x, top, slice, font, color, joined));
					joined = true;
					x += font.Measure(slice);
					_widest = MathF.Max(_widest, x);
					start += count;
				}
			}

			if (tokens is null || tokens.Count == 0) Emit(text.Length == 0 ? " " : text, Theme.EditorHoverForeground);
			else {
				string raw = block.Lines[index];

				foreach (Highlighter.ColoredToken token in tokens) {
					if (token.Start > column) Emit(raw[column..token.Start].Replace("\t", "    "), Theme.EditorHoverForeground);

					int end = Math.Min(raw.Length, token.Start + token.Length);

					if (end > token.Start) Emit(raw[token.Start..end].Replace("\t", "    "), token.Color);

					column = Math.Max(column, end);
				}

				if (column < raw.Length) Emit(raw[column..].Replace("\t", "    "), Theme.EditorHoverForeground);
			}

			y += line;
		}

		return y;
	}

	private float Table(MarkdownBlock block, float left, float width, float y, List<string> hrefs) {
		int columns = block.Rows.Max(static row => row.Count);
		float[] widths = new float[columns];
		float padding = CellPadding * _scale;
		float spacing = CellSpacing * _scale;

		for (int r = 0; r < block.Rows.Count; r++) {
			for (int c = 0; c < block.Rows[r].Count; c++) {
				float measured = 0f;

				foreach (Atom atom in Atoms(block.Rows[r][c], FontSize, r == 0, [])) measured += atom.Width;

				widths[c] = MathF.Max(widths[c], measured);
			}
		}

		y += spacing;

		for (int r = 0; r < block.Rows.Count; r++) {
			float x = left + spacing;
			float bottom = y + padding + Line;

			for (int c = 0; c < columns; c++) {
				List<InlineSpan> cell = c < block.Rows[r].Count ? block.Rows[r][c] : [];
				CellAlignment alignment = c < block.Alignments.Count && block.Alignments[c] != CellAlignment.Left ? block.Alignments[c] : r == 0 ? CellAlignment.Center : CellAlignment.Left;
				float cellBottom = Flow(cell, x + padding, widths[c] + 0.01f, y + padding, FontSize, Line, r == 0, Theme.EditorHoverForeground, alignment, hrefs, joinFirst: c > 0);
				bottom = MathF.Max(bottom, cellBottom);
				x += widths[c] + (padding * 2f) + spacing;
			}

			_widest = MathF.Max(_widest, x);
			y = bottom + padding + spacing;
		}

		return y;
	}
}
