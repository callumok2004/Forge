using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.IndentRainbow;

public enum IndentKind
{
	Level,

	Error,

	TabMix,
}

public readonly record struct IndentSpan(int Line, int Start, int End, int Depth, IndentKind Kind);

public static class IndentRainbow
{
	public static readonly uint[] DefaultColors =
	[
		0x12FFFF40,
		0x127FFF7F,
		0x12FF7FFF,
		0x124FECEC,
	];

	public const uint DefaultErrorColor = 0x4D802020;

	public static List<IndentSpan> Scan(string text, int tabSize, int levels, bool detectTabMix = false,
		bool colorOnWhiteSpaceOnly = false) {
		List<IndentSpan> spans = [];

		if (tabSize <= 0 || levels <= 0) return spans;

		int line = 0;
		int lineStart = 0;

		for (int i = 0; i <= text.Length; i++) {
			if (i < text.Length && text[i] != '\n') continue;

			int end = lineStart;

			while (end < i && (text[end] == ' ' || text[end] == '\t')) end++;

			if (end > lineStart) {
				ReadIndent(text, spans, line, lineStart, end, tabSize, levels, detectTabMix,
					colorOnWhiteSpaceOnly);
			}

			line++;
			lineStart = i + 1;
		}

		return spans;
	}

	private static void ReadIndent(string text, List<IndentSpan> spans, int line, int start, int end,
		int tabSize, int levels, bool detectTabMix, bool colorOnWhiteSpaceOnly) {
		int length = end - start;

		int width = 0;
		bool hasTab = false;
		bool hasSpace = false;

		for (int i = start; i < end; i++) {
			if (text[i] == '\t') {
				hasTab = true;
				width += tabSize;
			}
			else {
				hasSpace = true;
				width++;
			}
		}

		if (width % tabSize != 0) {
			spans.Add(new IndentSpan(line, 0, length, 0, IndentKind.Error));
			return;
		}

		bool mixed = detectTabMix && hasTab && hasSpace;

		int offset = 0;
		int depth = 0;

		while (offset < length) {
			int from = offset;

			offset += text[start + offset] == '\t' ? 1 : tabSize;

			if (colorOnWhiteSpaceOnly && offset > length) offset = length;

			spans.Add(new IndentSpan(line, from, offset, depth,
				mixed ? IndentKind.TabMix : IndentKind.Level));

			depth++;
		}
	}

	public static void ScanLine(ReadOnlySpan<char> text, int line, int tabSize, int levels, List<IndentSpan> into) {
		if (tabSize <= 0 || levels <= 0) return;

		int end = 0;

		while (end < text.Length && text[end] is ' ' or '	') end++;

		if (end == 0) return;

		int width = 0;

		for (int i = 0; i < end; i++) width += text[i] == '	' ? tabSize : 1;

		if (width % tabSize != 0) {
			into.Add(new IndentSpan(line, 0, end, 0, IndentKind.Error));
			return;
		}

		int offset = 0;
		int depth = 0;

		while (offset < end) {
			int from = offset;
			offset += text[offset] == '	' ? 1 : tabSize;
			into.Add(new IndentSpan(line, from, Math.Min(offset, end), depth++, IndentKind.Level));
		}
	}

	public static uint ColorOf(IndentSpan span, uint[] colors, uint errorColor, uint tabMixColor) =>
		span.Kind switch {
			IndentKind.Error => errorColor,
			IndentKind.TabMix => tabMixColor,
			_ => colors.Length == 0 ? 0u : colors[span.Depth % colors.Length],
		};
}
