using System.Text.RegularExpressions;

namespace Forge.Extensions.Dotnet.Layout;

public static partial class CSharpSource
{
	private static readonly HashSet<string> MemberModifiers = [
		"public", "private", "protected", "internal", "static", "readonly", "volatile", "const", "new", "unsafe",
		"required", "partial", "override", "virtual", "abstract", "sealed", "extern", "ref", "file", "fixed", "event",
	];

	public static string Blank(string text) {
		char[] output = text.ToCharArray();
		int length = text.Length;
		int i = 0;
		bool lineStart = true;

		while (i < length) {
			char c = text[i];

			if (lineStart) {
				int j = i;

				while (j < length && text[j] is ' ' or '\t') j++;

				if (j < length && text[j] == '#') {
					int end = j;

					while (end < length && text[end] != '\n') end++;

					Fill(output, j, end);
					i = end;
					continue;
				}

				lineStart = false;
			}

			if (c == '\n') {
				lineStart = true;
				i++;
				continue;
			}

			if (c == '/' && At(text, i + 1) == '/') {
				int end = i;

				while (end < length && text[end] != '\n') end++;

				Fill(output, i, end);
				i = end;
				continue;
			}

			if (c == '/' && At(text, i + 1) == '*') {
				int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
				int end = close < 0 ? length : close + 2;

				Fill(output, i, end);
				i = end;
				continue;
			}

			if (c == '"' || (c == '@' && At(text, i + 1) == '"') || (c == '$' && At(text, i + 1) is '"' or '@')) {
				int j = i;
				bool verbatim = false;

				while (At(text, j) is '$' or '@') {
					verbatim |= text[j] == '@';
					j++;
				}

				if (text.AsSpan(j).StartsWith("\"\"\"")) {
					int close = text.IndexOf("\"\"\"", j + 3, StringComparison.Ordinal);
					int end = close < 0 ? length : close + 3;

					Fill(output, i, end);
					i = end;
					continue;
				}

				j++;

				while (j < length) {
					if (!verbatim && text[j] == '\\') {
						j += 2;
						continue;
					}

					if (text[j] == '"') {
						if (verbatim && At(text, j + 1) == '"') {
							j += 2;
							continue;
						}

						break;
					}

					if (!verbatim && text[j] == '\n') break;

					j++;
				}

				Fill(output, i, j + 1);
				i = j + 1;
				continue;
			}

			if (c == '\'') {
				int j = i + 1;

				while (j < length && text[j] != '\'' && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;

				Fill(output, i, j + 1);
				i = j + 1;
				continue;
			}

			i++;
		}

		BlankInactiveBranches(text, output);

		return new string(output);
	}

	private static char At(string text, int index) => (uint)index < (uint)text.Length ? text[index] : '\0';

	private static char At(ReadOnlySpan<char> text, int index) => (uint)index < (uint)text.Length ? text[index] : '\0';

	private static void Fill(char[] output, int from, int to) {
		for (int k = from; k < to && k < output.Length; k++)
			if (output[k] is not '\n' and not '\r') output[k] = ' ';
	}

	private static void BlankInactiveBranches(string text, char[] output) {
		Span<bool> taken = stackalloc bool[64];
		int depth = 0;
		int line = 0;

		while (line < text.Length) {
			int end = text.IndexOf('\n', line);

			if (end < 0) end = text.Length;

			int i = line;

			while (i < end && text[i] is ' ' or '\t') i++;

			if (i < end && text[i] == '#') {
				ReadOnlySpan<char> directive = text.AsSpan(i + 1, end - i - 1).TrimStart();
				int wordLength = 0;

				while (wordLength < directive.Length && char.IsAsciiLetterLower(directive[wordLength])) wordLength++;

				ReadOnlySpan<char> word = directive[..wordLength];

				if (word.SequenceEqual("if")) {
					if (depth < taken.Length) taken[depth] = true;

					depth++;
				}
				else if ((word.SequenceEqual("elif") || word.SequenceEqual("else")) && depth > 0 && depth <= taken.Length) taken[depth - 1] = false;
				else if (word.SequenceEqual("endif") && depth > 0) depth--;
			}
			else if (taken[..Math.Min(depth, taken.Length)].Contains(false)) Fill(output, line, end);

			line = end + 1;
		}
	}

	public static int Matching(ReadOnlySpan<char> text, int open) {
		char opening = text[open];
		char closing = opening switch { '{' => '}', '(' => ')', '[' => ']', _ => '>' };
		int depth = 0;

		for (int i = open; i < text.Length; i++) {
			if (text[i] == opening) depth++;
			else if (text[i] == closing && --depth == 0) return i;
		}

		return text.Length - 1;
	}

	public static int SkipWhitespace(ReadOnlySpan<char> text, int i) {
		while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

		return i;
	}

	private static int IdentifierLength(ReadOnlySpan<char> text) {
		if (text.IsEmpty || !(char.IsAsciiLetter(text[0]) || text[0] == '_')) return 0;

		int length = 1;

		while (length < text.Length && (char.IsAsciiLetterOrDigit(text[length]) || text[length] == '_')) length++;

		return length;
	}

	private static (string Text, int End) Attributes(string text, int i) {
		int start = -1;
		int end = -1;

		i = SkipWhitespace(text, i);

		while (At(text, i) == '[') {
			int close = Matching(text, i);

			if (start < 0) start = i;

			end = close + 1;
			i = SkipWhitespace(text, close + 1);
		}

		return (start < 0 ? string.Empty : CollapseAttributes(text, start, end), i);
	}

	private static string CollapseAttributes(string text, int start, int end) {
		Span<char> buffer = end - start <= 512 ? stackalloc char[end - start] : new char[end - start];
		int written = 0;
		int depth = 0;

		for (int i = start; i < end; i++) {
			char c = text[i];

			if (c == '[') depth++;
			else if (c == ']') depth--;

			if (depth == 0 && c != ']' && char.IsWhiteSpace(c)) continue;

			buffer[written++] = c;
		}

		return new string(buffer[..written]);
	}

	private static int ReadType(ReadOnlySpan<char> text, int i) {
		i = SkipWhitespace(text, i);

		if (At(text, i) == '(') i = Matching(text, i) + 1;
		else {
			while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or ':' or '@')) i++;

			int j = SkipWhitespace(text, i);

			if (At(text, j) == '<') i = Matching(text, j) + 1;
		}

		while (true) {
			int j = SkipWhitespace(text, i);

			if (At(text, j) is '?' or '*') i = j + 1;
			else if (At(text, j) == '[' && IsRankSpecifier(text[j..])) i = Matching(text, j) + 1;
			else return i;
		}
	}

	private static bool IsRankSpecifier(ReadOnlySpan<char> text) {
		int i = 1;

		while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;

		return At(text, i) == ']';
	}

	public static List<TextPart> SplitTopLevel(ReadOnlySpan<char> text, char separator) {
		List<TextPart> parts = [];
		int depth = 0;
		int start = 0;

		for (int i = 0; i <= text.Length; i++) {
			char c = i < text.Length ? text[i] : separator;

			if (i < text.Length && c is '<' or '(' or '[' or '{') depth++;
			else if (i < text.Length && c is '>' or ')' or ']' or '}') depth--;
			else if (c == separator && (depth == 0 || i == text.Length)) {
				ReadOnlySpan<char> piece = text[start..i];
				int lead = piece.Length - piece.TrimStart().Length;
				ReadOnlySpan<char> trimmed = piece.Trim();

				if (!trimmed.IsEmpty) parts.Add(new TextPart(trimmed.ToString(), start + lead));

				start = i + 1;
			}
		}

		return parts;
	}

	public static (string Name, List<TextPart> Arguments) SplitGenericArguments(string typeText) {
		int open = typeText.IndexOf('<');

		if (open < 0 || !typeText.AsSpan().TrimEnd().EndsWith(">")) return (typeText.Trim(), []);

		int close = Matching(typeText, open);
		List<TextPart> arguments = SplitTopLevel(typeText.AsSpan(open + 1, close - open - 1), ',');

		for (int i = 0; i < arguments.Count; i++) arguments[i] = arguments[i] with { Offset = arguments[i].Offset + open + 1 };

		return (typeText[..open].Trim(), arguments);
	}

	private static void ParseField(string text, int start, int end, List<FieldDeclaration> output, bool forceEvent = false) {
		(string attributes, int i) = Attributes(text, start);
		bool isStatic = false;
		bool isFixed = false;
		bool isEvent = forceEvent;

		while (true) {
			int length = IdentifierLength(text.AsSpan(i, Math.Max(0, end - i)));

			if (length == 0 || !MemberModifiers.TryGetValue(text.Substring(i, length), out string? modifier)) break;

			isStatic |= modifier is "static" or "const";
			isFixed |= modifier == "fixed";
			isEvent |= modifier == "event";
			i = SkipWhitespace(text, i + length);
		}

		if (isStatic) return;

		int typeStart = SkipWhitespace(text, i);
		int typeEnd = Math.Min(ReadType(text.AsSpan(0, end), typeStart), end);
		string typeText = text[typeStart..typeEnd].Trim();

		if (typeText.Length == 0) return;

		int? fieldOffset = FieldOffsetPattern().Match(attributes) is { Success: true } offset ? int.Parse(offset.Groups[1].ValueSpan) : null;

		foreach (TextPart declarator in SplitTopLevel(text.AsSpan(typeEnd, end - typeEnd), ',')) {
			ReadOnlySpan<char> part = declarator.Text;
			int at = part.StartsWith("@") ? 1 : 0;
			int nameLength = IdentifierLength(part[at..]);

			if (nameLength == 0) continue;

			int cursor = SkipWhitespace(part, at + nameLength);
			string? fixedCount = null;

			if (At(part, cursor) == '[') {
				int close = part[cursor..].IndexOf(']');

				if (close > 0) {
					fixedCount = part.Slice(cursor + 1, close - 1).Trim().ToString();
					cursor += close + 1;
				}
			}

			ReadOnlySpan<char> rest = part[cursor..].Trim();

			output.Add(new FieldDeclaration {
				Initializer = rest.StartsWith("=") ? rest[1..].Trim().ToString() : null,
				Name = part.Slice(at, nameLength).ToString(),
				TypeText = typeText,
				TypeOffset = typeStart,
				NameOffset = typeEnd + declarator.Offset,
				FixedCount = isFixed ? fixedCount : null,
				FieldOffset = fieldOffset,
				IsEvent = isEvent,
			});
		}
	}

	private static int EndOfStatement(ReadOnlySpan<char> text, int from, int limit) {
		int depth = 0;

		for (int i = from; i < limit; i++) {
			char c = text[i];

			if (c is '(' or '[' or '{') depth++;
			else if (c is ')' or ']' or '}') depth--;
			else if (c == ';' && depth <= 0) return i;
		}

		return limit;
	}

	private static bool IsAutoAccessorList(ReadOnlySpan<char> body) {
		ReadOnlySpan<char> inner = body[1..^1];

		if (inner.Contains("=>", StringComparison.Ordinal) || inner.Contains('{')) return FieldKeyword().IsMatch(inner);

		return AutoAccessor().IsMatch(inner);
	}

	public static List<FieldDeclaration> ParseTypeBody(string text, int bodyOpen, int bodyClose) {
		List<FieldDeclaration> fields = [];
		int i = bodyOpen + 1;

		while (i < bodyClose) {
			i = SkipWhitespace(text, i);

			if (i >= bodyClose) break;

			int memberStart = i;
			int j = i;
			int depth = 0;

			for (; j < bodyClose; j++) {
				char c = text[j];

				if (c is '(' or '[' or '<') depth++;
				else if (c is ')' or ']' or '>') depth = Math.Max(0, depth - 1);
				else if (depth == 0 && c is ';' or '{' or '=') break;
			}

			int headerStart = Attributes(text, memberStart).End;
			ReadOnlySpan<char> header = text.AsSpan(headerStart, Math.Max(0, j - headerStart));
			bool isOperator = OperatorKeyword().IsMatch(header);

			if (At(text, j) == '=' && At(text, j + 1) != '>') {
				int end = EndOfStatement(text, j, bodyClose);

				if (!isOperator) ParseField(text, memberStart, end, fields);

				i = end + 1;
				continue;
			}

			if (At(text, j) == '=' && At(text, j + 1) == '>') {
				int semicolon = text.IndexOf(';', j);
				i = semicolon < 0 ? bodyClose : semicolon + 1;
				continue;
			}

			if (At(text, j) == ';') {
				int paren = header.IndexOf('(');
				ReadOnlySpan<char> beforeParen = paren < 0 ? header : header[..paren];
				bool isMethod = MethodHeaderEnd().IsMatch(header.Trim()) && !beforeParen.Contains('=');

				if (!isMethod && !isOperator) ParseField(text, memberStart, j, fields);

				i = j + 1;
				continue;
			}

			int close = Matching(text, j);
			bool isType = NestedTypeKeyword().IsMatch(header);
			bool isMethodBody = MethodHeaderEndMultiline().IsMatch(header.Trim()) || isOperator || IndexerHeader().IsMatch(header);
			bool isEventAccessors = EventKeyword().IsMatch(header);

			if (!isType && !isMethodBody && !isEventAccessors) {
				ReadOnlySpan<char> body = text.AsSpan(j, close - j + 1);
				string attributes = Attributes(text, memberStart).Text;

				if (IsAutoAccessorList(body) || (PartialKeyword().IsMatch(header) && NetworkVar().IsMatch(attributes))) ParseField(text, memberStart, j, fields);
			}

			i = close + 1;
			int after = SkipWhitespace(text, i);

			if (At(text, after) == '=' && At(text, after + 1) != '>') {
				int semicolon = text.IndexOf(';', after);

				if (fields.Count > 0 && fields[^1].Initializer is null) fields[^1].Initializer = text[(after + 1)..(semicolon < 0 ? bodyClose : semicolon)].Trim();

				i = semicolon < 0 ? bodyClose : semicolon + 1;
			}
			else if (At(text, after) == ';') i = after + 1;
		}

		return fields;
	}

	public static TypeDeclaration? ParseTypeDeclaration(string text, int start) {
		(string attributes, int i) = Attributes(text, start);
		List<string> modifiers = [];
		TypeKind kind;

		while (true) {
			int length = IdentifierLength(text.AsSpan(i, Math.Min(64, text.Length - i)));

			if (length == 0) return null;

			ReadOnlySpan<char> word = text.AsSpan(i, length);
			i = SkipWhitespace(text, i + length);

			if (word.SequenceEqual("record")) {
				if (KeywordAt(text, i, "struct")) {
					kind = TypeKind.RecordStruct;
					i = SkipWhitespace(text, i + 6);
				}
				else if (KeywordAt(text, i, "class")) {
					kind = TypeKind.RecordClass;
					i = SkipWhitespace(text, i + 5);
				}
				else kind = TypeKind.RecordClass;

				break;
			}

			if (word.SequenceEqual("class")) {
				kind = TypeKind.Class;
				break;
			}

			if (word.SequenceEqual("struct")) {
				kind = TypeKind.Struct;
				break;
			}

			if (word.SequenceEqual("enum")) {
				kind = TypeKind.Enum;
				break;
			}

			if (word.SequenceEqual("interface")) {
				kind = TypeKind.Interface;
				break;
			}

			string modifier = word.ToString();

			if (modifier != "readonly" && !MemberModifiers.Contains(modifier)) return null;

			modifiers.Add(modifier);
		}

		int at = At(text, i) == '@' ? i + 1 : i;
		int nameLength = IdentifierLength(text.AsSpan(at));

		if (nameLength == 0) return null;

		int nameOffset = i;
		string name = text.Substring(at, nameLength);
		i = at + nameLength;

		List<string> typeParameters = [];
		int k = SkipWhitespace(text, i);

		if (At(text, k) == '<') {
			int close = Matching(text, k);

			foreach (TextPart parameter in SplitTopLevel(text.AsSpan(k + 1, close - k - 1), ',')) {
				ReadOnlySpan<char> value = parameter.Text;

				if (value.StartsWith("in ") || value.StartsWith("out ")) value = value[(value.IndexOf(' ') + 1)..].TrimStart();

				typeParameters.Add(value.ToString());
			}

			k = SkipWhitespace(text, close + 1);
		}

		List<FieldDeclaration> fields = [];

		if (At(text, k) == '(') {
			int close = Matching(text, k);

			foreach (TextPart parameter in SplitTopLevel(text.AsSpan(k + 1, close - k - 1), ',')) {
				int equals = parameter.Text.IndexOf('=');
				int length = equals < 0 ? parameter.Text.Length : equals;
				int parameterStart = k + 1 + parameter.Offset;

				ParseField(text, parameterStart, parameterStart + length, fields);
			}

			k = SkipWhitespace(text, close + 1);
		}

		int open = text.IndexOf('{', k);
		int semicolon = text.IndexOf(';', k);
		int headerEnd = open < 0 || (semicolon >= 0 && semicolon < open) ? semicolon : open;

		if (headerEnd < 0) return null;

		List<TextPart> baseList = [];
		TextPart? enumBase = null;
		ReadOnlySpan<char> tail = text.AsSpan(k, headerEnd - k);
		int colon = tail.IndexOf(':');

		if (colon >= 0) {
			ReadOnlySpan<char> list = tail[(colon + 1)..];
			int where = WhereKeyword().Match(list.ToString()) is { Success: true } found ? found.Index : -1;

			if (where >= 0) list = list[..where];

			foreach (TextPart entry in SplitTopLevel(list, ',')) {
				int paren = entry.Text.IndexOf('(');
				baseList.Add(new TextPart((paren < 0 ? entry.Text : entry.Text[..paren]).Trim(), k + colon + 1 + entry.Offset));
			}

			if (kind == TypeKind.Enum && baseList.Count > 0) enumBase = baseList[0];
		}

		int bodyStart = -1;
		int bodyEnd = -1;

		if (headerEnd == open) {
			bodyStart = open;
			bodyEnd = Matching(text, open);

			if (kind is not TypeKind.Enum and not TypeKind.Interface) fields.AddRange(ParseTypeBody(text, open, bodyEnd));
		}

		return new TypeDeclaration {
			Kind = kind,
			Name = name,
			TypeParameters = typeParameters,
			Attributes = attributes,
			Modifiers = modifiers,
			BaseList = baseList,
			EnumBase = enumBase,
			Fields = fields,
			NameOffset = nameOffset,
			BodyStart = bodyStart,
			BodyEnd = bodyEnd,
		};
	}

	private static bool KeywordAt(string text, int i, string keyword) => text.AsSpan(i).StartsWith(keyword) && !(char.IsAsciiLetterOrDigit(At(text, i + keyword.Length)) || At(text, i + keyword.Length) == '_');

	public static int DeclarationStart(string blanked, int keywordOffset) {
		int start = blanked.LastIndexOf('\n', Math.Max(0, keywordOffset - 1)) + 1;

		while (true) {
			int previousEnd = start - 1;

			if (previousEnd <= 0) return start;

			int previousStart = blanked.LastIndexOf('\n', previousEnd - 1) + 1;
			ReadOnlySpan<char> line = blanked.AsSpan(previousStart, previousEnd - previousStart).Trim();

			if (line.StartsWith("[") && line.EndsWith("]")) start = previousStart;
			else return start;
		}
	}

	public static List<(TypeDeclaration Declaration, int Start)> ScanTypeDeclarations(string blanked) {
		List<(TypeDeclaration, int)> found = [];
		HashSet<int> seen = [];

		foreach (ValueMatch match in TypeKeyword().EnumerateMatches(blanked)) {
			int start = DeclarationStart(blanked, match.Index);

			if (!seen.Add(start)) continue;

			if (ParseTypeDeclaration(blanked, start) is { } declaration) found.Add((declaration, start));
		}

		return found;
	}

	[GeneratedRegex(@"FieldOffset\s*\(\s*(\d+)\s*\)")]
	private static partial Regex FieldOffsetPattern();

	[GeneratedRegex(@"\bfield\b")]
	private static partial Regex FieldKeyword();

	[GeneratedRegex(@"\b(get|set|init)\s*;")]
	private static partial Regex AutoAccessor();

	[GeneratedRegex(@"\boperator\b")]
	private static partial Regex OperatorKeyword();

	[GeneratedRegex(@"\)\s*(where\b[^;]*)?$")]
	private static partial Regex MethodHeaderEnd();

	[GeneratedRegex(@"\)\s*(where\b.*)?$", RegexOptions.Singleline)]
	private static partial Regex MethodHeaderEndMultiline();

	[GeneratedRegex(@"\bthis\s*\[")]
	private static partial Regex IndexerHeader();

	[GeneratedRegex(@"\b(class|struct|interface|enum|record|delegate)\b")]
	private static partial Regex NestedTypeKeyword();

	[GeneratedRegex(@"\bevent\b")]
	private static partial Regex EventKeyword();

	[GeneratedRegex(@"\bpartial\b")]
	private static partial Regex PartialKeyword();

	[GeneratedRegex(@"\bNetworkVar\b")]
	private static partial Regex NetworkVar();

	[GeneratedRegex(@"\bwhere\b")]
	private static partial Regex WhereKeyword();

	[GeneratedRegex(@"\b(class|struct|record|enum|interface)\s+@?[A-Za-z_]")]
	private static partial Regex TypeKeyword();
}
