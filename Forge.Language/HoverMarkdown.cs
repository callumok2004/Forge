using System.Text;

using Forge.Common.Render;

namespace Forge.Language;

[Flags]
public enum InlineStyle : byte
{
	None = 0,
	Bold = 1,
	Italic = 2,
	Code = 4,
}

public readonly record struct InlineSpan(string Text, InlineStyle Style, string? Href = null, Codicon? Icon = null, string? Image = null)
{
	public bool IsBreak => Text == "\n" && Icon is null && Image is null;
}

public enum BlockKind : byte
{
	Paragraph,
	Heading,
	Code,
	Rule,
	ListItem,
	Table,
}

public enum CellAlignment : byte
{
	Left,
	Center,
	Right,
}

public sealed class MarkdownBlock(BlockKind kind)
{
	public BlockKind Kind { get; } = kind;

	public List<InlineSpan> Spans { get; init; } = [];

	public int Level { get; init; }

	public string Marker { get; init; } = string.Empty;

	public string? Language { get; init; }

	public List<string> Lines { get; init; } = [];

	public List<List<List<InlineSpan>>> Rows { get; init; } = [];

	public List<CellAlignment> Alignments { get; init; } = [];

	public string PlainText => HoverMarkdown.Plain(Spans);
}

public static class HoverMarkdown
{
	public const string RowSeparator = "\n\n\u001E\n\n";

	public static List<List<MarkdownBlock>> ParseRows(string markdown) {
		List<List<MarkdownBlock>> rows = [];

		foreach (string part in markdown.Split('\u001E')) {
			List<MarkdownBlock> blocks = Parse(part);

			if (blocks.Count > 0) rows.Add(blocks);
		}

		return rows;
	}

	private static bool IsRule(ReadOnlySpan<char> line) {
		line = line.Trim();

		if (line.Length < 3 || line[0] is not ('-' or '*' or '_')) return false;

		char marker = line[0];
		int count = 0;

		foreach (char character in line) {
			if (character == marker) count++;
			else if (character is not (' ' or '\t')) return false;
		}

		return count >= 3;
	}

	private static bool IsFence(ReadOnlySpan<char> line, out char fence, out int length, out string language) {
		ReadOnlySpan<char> trimmed = line.TrimStart();
		fence = trimmed.Length > 0 ? trimmed[0] : '\0';
		length = 0;
		language = string.Empty;

		if (fence is not ('`' or '~')) return false;

		while (length < trimmed.Length && trimmed[length] == fence) length++;

		if (length < 3) return false;

		language = trimmed[length..].Trim().ToString();

		if (fence == '`' && language.Contains('`')) return false;

		int space = language.IndexOf(' ');

		if (space >= 0) language = language[..space];

		return true;
	}

	private static bool IsListItem(string line, out int indent, out string marker, out string content) {
		indent = 0;

		while (indent < line.Length && line[indent] == ' ') indent++;

		marker = string.Empty;
		content = string.Empty;

		int at = indent;

		if (at < line.Length && line[at] is '*' or '-' or '+') {
			if (at + 1 < line.Length && line[at + 1] is not (' ' or '\t')) return false;

			if (at + 1 >= line.Length) return false;

			marker = "•";
			content = line[(at + 2)..];
			return true;
		}

		int digits = at;

		while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;

		if (digits == at || digits - at > 9 || digits >= line.Length || line[digits] is not ('.' or ')') || digits + 1 >= line.Length || line[digits + 1] != ' ') return false;

		marker = line[at..(digits + 1)];
		content = line[(digits + 2)..];
		return true;
	}

	private static bool IsTableRow(ReadOnlySpan<char> line) => line.Trim().Length > 1 && line.Trim()[0] == '|';

	private static bool IsDelimiterRow(ReadOnlySpan<char> row) {
		row = row.Trim();
		return row.Length > 0 && row.IndexOfAnyExcept("|-: \t") < 0 && row.Contains('-');
	}

	public static List<MarkdownBlock> Parse(string markdown) {
		List<MarkdownBlock> blocks = [];
		string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		StringBuilder paragraph = new();

		void Flush() {
			if (paragraph.Length == 0) return;

			blocks.Add(new MarkdownBlock(BlockKind.Paragraph) { Spans = Inline(paragraph.ToString()) });
			paragraph.Clear();
		}

		for (int i = 0; i < lines.Length; i++) {
			string line = lines[i];
			ReadOnlySpan<char> trimmed = line.AsSpan().Trim();

			if (IsFence(line, out char fence, out int fenceLength, out string language)) {
				Flush();
				List<string> code = [];

				for (i++; i < lines.Length; i++) {
					ReadOnlySpan<char> closing = lines[i].AsSpan().Trim();

					if (closing.Length >= fenceLength && closing.IndexOfAnyExcept(fence) < 0) break;

					code.Add(lines[i]);
				}

				blocks.Add(new MarkdownBlock(BlockKind.Code) { Language = language, Lines = code });
				continue;
			}

			if (trimmed.IsEmpty) {
				Flush();
				continue;
			}

			if (IsRule(trimmed)) {
				Flush();
				blocks.Add(new MarkdownBlock(BlockKind.Rule));
				continue;
			}

			if (trimmed[0] == '#') {
				int level = 0;

				while (level < trimmed.Length && trimmed[level] == '#') level++;

				if (level <= 6 && (level == trimmed.Length || trimmed[level] == ' ')) {
					Flush();
					blocks.Add(new MarkdownBlock(BlockKind.Heading) { Level = level, Spans = Inline(trimmed[level..].Trim().TrimEnd('#').Trim().ToString()) });
					continue;
				}
			}

			if (IsTableRow(trimmed) && i + 1 < lines.Length && IsDelimiterRow(lines[i + 1])) {
				Flush();
				MarkdownBlock table = new(BlockKind.Table);
				table.Rows.Add(Cells(trimmed));

				foreach (string alignment in RawCells(lines[i + 1].AsSpan().Trim())) {
					string cell = alignment.Trim();
					table.Alignments.Add(cell.StartsWith(':') && cell.EndsWith(':') ? CellAlignment.Center : cell.EndsWith(':') ? CellAlignment.Right : CellAlignment.Left);
				}

				for (i += 2; i < lines.Length && IsTableRow(lines[i]); i++) table.Rows.Add(Cells(lines[i].AsSpan().Trim()));

				i--;
				blocks.Add(table);
				continue;
			}

			if (IsListItem(line, out int indent, out string marker, out string content)) {
				Flush();
				StringBuilder item = new(content);

				while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !IsListItem(lines[i + 1], out _, out _, out _) && !IsFence(lines[i + 1], out _, out _, out _) && !IsRule(lines[i + 1])) {
					i++;
					AppendLine(item, lines[i]);
				}

				blocks.Add(new MarkdownBlock(BlockKind.ListItem) { Level = indent / 2, Marker = marker, Spans = Inline(item.ToString()) });
				continue;
			}

			AppendLine(paragraph, line);
		}

		Flush();
		return blocks;
	}

	private static void AppendLine(StringBuilder into, string line) {
		if (into.Length > 0) {
			bool hard = into.Length >= 2 && into[^1] == ' ' && into[^2] == ' ';
			bool backslash = into.Length >= 1 && into[^1] == '\\';

			if (backslash) into.Length--;

			while (into.Length > 0 && into[^1] is ' ' or '\t') into.Length--;

			into.Append(hard || backslash ? "<br>" : " ");
		}

		into.Append(line.TrimStart());
	}

	private static List<string> RawCells(ReadOnlySpan<char> row) {
		if (row.StartsWith("|")) row = row[1..];

		if (row.EndsWith("|") && !row.EndsWith("\\|")) row = row[..^1];

		List<string> cells = [];
		StringBuilder cell = new();
		bool inCode = false;

		for (int i = 0; i < row.Length; i++) {
			if (row[i] == '`') inCode = !inCode;

			if (row[i] == '\\' && i + 1 < row.Length && row[i + 1] == '|') {
				cell.Append('|');
				i++;
				continue;
			}

			if (row[i] == '|' && !inCode) {
				cells.Add(cell.ToString());
				cell.Clear();
				continue;
			}

			cell.Append(row[i]);
		}

		cells.Add(cell.ToString());
		return cells;
	}

	private static List<List<InlineSpan>> Cells(ReadOnlySpan<char> row) => [.. RawCells(row).Select(static cell => Inline(cell.Trim()))];

	public static string Plain(IEnumerable<InlineSpan> spans) {
		StringBuilder text = new();

		foreach (InlineSpan span in spans) {
			if (span.Icon is not null || span.Image is not null) continue;

			text.Append(span.Text);
		}

		return text.ToString();
	}

	private static bool IsPunctuation(char character) => character is > ' ' and < '\x7f' && !char.IsAsciiLetterOrDigit(character);

	public static List<InlineSpan> Inline(string text) {
		List<InlineSpan> spans = [];
		InlineParse(text, InlineStyle.None, null, spans);
		return Icons(Merge(spans));
	}

	private static List<InlineSpan> Merge(List<InlineSpan> spans) {
		List<InlineSpan> merged = [];

		foreach (InlineSpan span in spans) {
			if (span.Text.Length == 0 && span.Icon is null && span.Image is null) continue;

			if (merged.Count > 0 && merged[^1] is { Icon: null, Image: null } last && span.Icon is null && span.Image is null && !span.IsBreak && !last.IsBreak && last.Style == span.Style && last.Href == span.Href) {
				merged[^1] = last with { Text = last.Text + span.Text };
				continue;
			}

			merged.Add(span);
		}

		return merged;
	}

	private static List<InlineSpan> Icons(List<InlineSpan> spans) {
		List<InlineSpan> result = [];

		foreach (InlineSpan span in spans) {
			if ((span.Style & InlineStyle.Code) != 0 || span.Icon is not null || span.Image is not null || !span.Text.Contains("$(", StringComparison.Ordinal)) {
				result.Add(span);
				continue;
			}

			string text = span.Text;
			int start = 0;

			for (int at = text.IndexOf("$(", StringComparison.Ordinal); at >= 0; at = text.IndexOf("$(", at + 1, StringComparison.Ordinal)) {
				int close = text.IndexOf(')', at + 2);

				if (close < 0) break;

				string name = text[(at + 2)..close];
				int modifier = name.IndexOf('~');

				if (modifier >= 0) name = name[..modifier];

				if (name.Length == 0 || !name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-') || !CodiconNames.TryGet(name, out Codicon icon)) continue;

				if (at > start) result.Add(span with { Text = text[start..at] });

				result.Add(span with { Text = string.Empty, Icon = icon });
				start = close + 1;
				at = close;
			}

			if (start < text.Length) result.Add(span with { Text = text[start..] });
		}

		return result;
	}

	private static int FindClosing(string text, int from, string delimiter) {
		for (int i = from; i <= text.Length - delimiter.Length; i++) {
			if (text[i] == '\\') {
				i++;
				continue;
			}

			if (text[i] == '`') {
				int close = text.IndexOf('`', i + 1);

				if (close > 0) i = close;

				continue;
			}

			if (string.CompareOrdinal(text, i, delimiter, 0, delimiter.Length) == 0 && i > from && !char.IsWhiteSpace(text[i - 1])) {
				if (delimiter[0] == '_' && i + delimiter.Length < text.Length && char.IsLetterOrDigit(text[i + delimiter.Length])) continue;

				if (delimiter.Length == 1 && i + 1 < text.Length && text[i + 1] == delimiter[0]) {
					i++;
					continue;
				}

				return i;
			}
		}

		return -1;
	}

	private static int MatchBracket(string text, int open) {
		int depth = 0;

		for (int i = open; i < text.Length; i++) {
			if (text[i] == '\\') {
				i++;
				continue;
			}

			if (text[i] == '[') depth++;
			else if (text[i] == ']' && --depth == 0) return i;
		}

		return -1;
	}

	private static int MatchParen(string text, int open) {
		int depth = 0;

		for (int i = open; i < text.Length; i++) {
			if (text[i] == '\\') {
				i++;
				continue;
			}

			if (text[i] == '(') depth++;
			else if (text[i] == ')' && --depth == 0) return i;
		}

		return -1;
	}

	private static string Destination(string raw) {
		string target = raw.Trim();
		int space = target.IndexOf(' ');

		if (space > 0 && target[(space + 1)..].TrimStart().StartsWith('"')) target = target[..space];

		if (target.StartsWith('<') && target.EndsWith('>')) target = target[1..^1];

		StringBuilder unescaped = new(target.Length);

		for (int i = 0; i < target.Length; i++) {
			if (target[i] == '\\' && i + 1 < target.Length && IsPunctuation(target[i + 1])) i++;

			unescaped.Append(target[i]);
		}

		return unescaped.ToString();
	}

	private static void InlineParse(string text, InlineStyle style, string? href, List<InlineSpan> into) {
		StringBuilder plain = new();

		void Emit() {
			if (plain.Length == 0) return;

			into.Add(new InlineSpan(plain.ToString(), style, href));
			plain.Clear();
		}

		for (int i = 0; i < text.Length; i++) {
			char character = text[i];

			if (character == '\\' && i + 1 < text.Length && IsPunctuation(text[i + 1])) {
				plain.Append(text[++i]);
				continue;
			}

			if (character == '`') {
				int ticks = 1;

				while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;

				string fence = new('`', ticks);
				int close = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);

				if (close > 0) {
					Emit();
					string code = text[(i + ticks)..close];

					if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];

					into.Add(new InlineSpan(code, style | InlineStyle.Code, href));
					i = close + ticks - 1;
					continue;
				}

				plain.Append(fence);
				i += ticks - 1;
				continue;
			}

			if ((character == '!' && i + 1 < text.Length && text[i + 1] == '[') || character == '[') {
				int open = character == '!' ? i + 1 : i;
				int closeBracket = MatchBracket(text, open);

				if (closeBracket > 0 && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(' && MatchParen(text, closeBracket + 1) is var closeParen and > 0) {
					Emit();
					string label = text[(open + 1)..closeBracket];
					string target = Destination(text[(closeBracket + 2)..closeParen]);

					if (character == '!') into.Add(new InlineSpan(label, style, href, Image: target));
					else InlineParse(label, style, target, into);

					i = closeParen;
					continue;
				}
			}

			if (character is '*' or '_') {
				bool strong = i + 1 < text.Length && text[i + 1] == character;
				string delimiter = strong ? new string(character, 2) : character.ToString();
				int start = i + delimiter.Length;
				bool leftFlanking = start < text.Length && !char.IsWhiteSpace(text[start]);
				bool intraword = character == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]);

				if (leftFlanking && !intraword && FindClosing(text, start, delimiter) is var end and > 0) {
					Emit();
					InlineParse(text[start..end], style | (strong ? InlineStyle.Bold : InlineStyle.Italic), href, into);
					i = end + delimiter.Length - 1;
					continue;
				}
			}

			if (character == '<') {
				if (string.Compare(text, i, "<br>", 0, 4, StringComparison.OrdinalIgnoreCase) == 0 || string.Compare(text, i, "<br/>", 0, 5, StringComparison.OrdinalIgnoreCase) == 0 || string.Compare(text, i, "<br />", 0, 6, StringComparison.OrdinalIgnoreCase) == 0) {
					Emit();
					into.Add(new InlineSpan("\n", style, href));
					i = text.IndexOf('>', i);
					continue;
				}
			}

			if (character == '&') {
				int semicolon = text.IndexOf(';', i);

				if (semicolon > i && semicolon - i <= 8 && Entity(text[(i + 1)..semicolon]) is { } decoded) {
					plain.Append(decoded);
					i = semicolon;
					continue;
				}
			}

			plain.Append(character == '\t' ? ' ' : character);
		}

		Emit();
	}

	private static string? Entity(string name) => name switch {
		"nbsp" => " ",
		"lt" => "<",
		"gt" => ">",
		"amp" => "&",
		"quot" => "\"",
		"apos" or "#39" => "'",
		_ when name.StartsWith("#x", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int hex) => char.ConvertFromUtf32(hex),
		_ when name.StartsWith('#') && int.TryParse(name.AsSpan(1), out int code) => char.ConvertFromUtf32(code),
		_ => null,
	};
}
