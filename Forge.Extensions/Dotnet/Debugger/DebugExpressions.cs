using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Server;

namespace Forge.Extensions.Dotnet.Debugger;

public static class DebugExpressions
{
	private static readonly HashSet<string> Keywords = [
		"abstract", "add", "alias", "allows", "and", "args", "as", "ascending", "async", "await", "base", "bool", "break",
		"by", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default",
		"delegate", "descending", "do", "double", "dynamic", "else", "enum", "equals", "event", "explicit",
		"extension", "extern", "false", "file", "finally", "fixed", "float", "for", "foreach", "from", "get", "global",
		"goto", "group", "if", "implicit", "in", "init", "int", "interface", "internal", "into", "is", "join",
		"let", "lock", "long", "managed", "nameof", "namespace", "new", "nint", "not", "notnull", "nuint",
		"null", "object", "on", "operator", "or", "orderby", "out", "override", "params", "partial", "private",
		"protected", "public", "readonly", "record", "ref", "remove", "required", "return", "sbyte", "sealed",
		"scoped", "select", "set", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "throw",
		"true", "try", "typeof", "uint", "ulong", "unchecked", "unmanaged", "unsafe", "ushort", "using",
		"value", "var", "virtual", "void", "volatile", "when", "where", "while", "with", "yield",
	];

	private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KeywordLookup = Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

	private static bool IsKeyword(ReadOnlySpan<char> word) => KeywordLookup.Contains(word);

	private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

	private static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

	public static void StripNonCode(ReadOnlySpan<char> line, Span<char> output) {
		int index = 0;

		while (index < line.Length) {
			char c = line[index];

			if (c == '/' && index + 1 < line.Length && line[index + 1] == '/') {
				output[index..].Fill(' ');
				return;
			}

			if (c is '"' or '\'') {
				int end = index + 1;

				while (end < line.Length) {
					if (line[end] == '\\') {
						end += 2;
						continue;
					}

					if (line[end] == c) {
						end++;
						break;
					}

					end++;
				}

				end = Math.Min(end, line.Length);
				output[index..end].Fill(' ');
				index = end;
				continue;
			}

			output[index] = c;
			index++;
		}
	}

	private static bool StartsWithCallOrIndexer(ReadOnlySpan<char> text) {
		text = text.TrimStart();

		return !text.IsEmpty && text[0] is '(' or '[';
	}

	private static bool EndsWithMemberAccess(ReadOnlySpan<char> text, out int start) {
		start = -1;
		text = text.TrimEnd();

		if (text.EndsWith("?.")) text = text[..^2];
		else if (text.EndsWith(".")) text = text[..^1];
		else return false;

		text = text.TrimEnd();
		int end = text.Length;
		int begin = end;

		while (begin > 0 && IsIdentifierPart(text[begin - 1])) begin--;

		if (begin == end || !IsIdentifierStart(text[begin])) return false;

		start = begin;
		return true;
	}

	private static bool HasMemberAccessBefore(ReadOnlySpan<char> text) {
		text = text.TrimEnd();

		return text.EndsWith(".");
	}

	public static (int Start, int End)? ExpressionRange(ReadOnlySpan<char> lineText, int column) {
		Span<char> line = lineText.Length <= 1024 ? stackalloc char[lineText.Length] : new char[lineText.Length];
		StripNonCode(lineText, line);

		int at = Math.Clamp(column, 0, line.Length);
		int wordStart = at;
		int wordEnd = at;

		while (wordStart > 0 && IsIdentifierPart(line[wordStart - 1])) wordStart--;

		while (wordEnd < line.Length && IsIdentifierPart(line[wordEnd])) wordEnd++;

		while (wordStart < wordEnd && !IsIdentifierStart(line[wordStart])) wordStart++;

		if (wordStart >= wordEnd || IsKeyword(line[wordStart..wordEnd]) || StartsWithCallOrIndexer(line[wordEnd..])) return null;

		int start = wordStart;

		while (start > 0 && EndsWithMemberAccess(line[..start], out int previous)) {
			int identifierEnd = previous;

			while (identifierEnd < line.Length && IsIdentifierPart(line[identifierEnd])) identifierEnd++;

			ReadOnlySpan<char> identifier = line[previous..identifierEnd];

			if (IsKeyword(identifier) && !identifier.SequenceEqual("base") && !identifier.SequenceEqual("this")) break;

			start = previous;
		}

		return (start, wordEnd);
	}

	public static EvaluatableExpression? Evaluatable(string lineText, int line, int column, IReadOnlyList<ServerToken>? tokens) {
		if (ExpressionRange(lineText, column) is not (int start, int end)) return null;

		string text = lineText[start..end];
		bool qualify = false;

		if (tokens is not null && !text.Contains('.')) {
			foreach (ServerToken token in tokens) {
				if (token.Line != line || token.StartColumn > start || start >= token.StartColumn + token.Length) continue;

				qualify = token.Type is "field" or "property" && !token.IsStatic;
				break;
			}
		}

		return new EvaluatableExpression(start, end, qualify ? "this." + text : text);
	}

	public static void InlineValues(int firstLine, int lastLine, Func<int, string> lineText, List<InlineValueLookup> into) {
		HashSet<string> seen = [];
		HashSet<string>.AlternateLookup<ReadOnlySpan<char>> seenLookup = seen.GetAlternateLookup<ReadOnlySpan<char>>();

		Span<char> buffer = stackalloc char[1024];

		for (int lineNumber = firstLine; lineNumber <= lastLine; lineNumber++) {
			string raw = lineText(lineNumber);
			Span<char> line = raw.Length <= buffer.Length ? buffer[..raw.Length] : new char[raw.Length];
			StripNonCode(raw, line);
			seen.Clear();

			int index = 0;

			while (index < line.Length) {
				if (!IsIdentifierStart(line[index]) || (index > 0 && IsIdentifierPart(line[index - 1]))) {
					index++;
					continue;
				}

				int start = index;

				while (index < line.Length && IsIdentifierPart(line[index])) index++;

				ReadOnlySpan<char> name = line[start..index];

				if (IsKeyword(name) || seenLookup.Contains(name) || HasMemberAccessBefore(line[..start]) || StartsWithCallOrIndexer(line[index..])) continue;

				seenLookup.Add(name);
				into.Add(new InlineValueLookup(lineNumber, start, index, name.ToString()));
			}
		}
	}
}
