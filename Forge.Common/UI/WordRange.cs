namespace Forge.Common.UI;

public static class WordRange
{
	public const string EditorSeparators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";
	public const string TerminalSeparators = " ()[]{}',\"`─‘’“”|";

	private enum CharacterClass { Regular, Whitespace, Separator }

	private static CharacterClass Classify(char c, ReadOnlySpan<char> separators) {
		if (c is ' ' or '\t' or '\0') return CharacterClass.Whitespace;

		return separators.Contains(c) ? CharacterClass.Separator : CharacterClass.Regular;
	}

	public static (int Start, int End) At(ReadOnlySpan<char> text, int column, ReadOnlySpan<char> separators) {
		if (text.IsEmpty) return (0, 0);

		int at = Math.Clamp(column, 0, text.Length);

		if (at >= text.Length || (at > 0 && Classify(text[at], separators) != CharacterClass.Regular && Classify(text[at - 1], separators) == CharacterClass.Regular)) at--;

		CharacterClass kind = Classify(text[at], separators);

		if (kind == CharacterClass.Separator) return (at, at + 1);

		int start = at;
		int end = at + 1;

		while (start > 0 && Classify(text[start - 1], separators) == kind) start--;

		while (end < text.Length && Classify(text[end], separators) == kind) end++;

		return (start, end);
	}
}
