namespace Forge.Syntax;

public static class CodeTokenizer
{
	public static LineState Tokenize(
		ReadOnlySpan<char> line,
		LineState startState,
		LanguageDefinition language,
		List<Token> output) {
		if (language.IsMarkup) return TokenizeMarkup(line, startState, language, output);

		int position = 0;
		LineState state = startState;

		if (state is LineState.BlockComment or LineState.DocBlockComment) {
			position = ContinueBlockComment(line, language, state, output, ref state);
		}
		else if (state == LineState.MultiLineString) position = ContinueVerbatimString(line, output, ref state);

		while (position < line.Length) {
			char current = line[position];

			if (current is ' ' or '\t') {
				position++;
				continue;
			}

			int start = position;

			if (language.LineComment == "//" && Matches(line, position, "///")) {
				ScanDocComment(line[start..], start, output);
				return LineState.Normal;
			}

			if (Matches(line, position, language.LineComment)) {
				output.Add(new Token(start, line.Length - start, TokenKind.Comment));
				return LineState.Normal;
			}

			if (Matches(line, position, language.BlockCommentStart)) {
				bool isDoc = IsDocComment(line, position, language);
				state = isDoc ? LineState.DocBlockComment : LineState.BlockComment;

				position += language.BlockCommentStart!.Length;
				int end = IndexOf(line, position, language.BlockCommentEnd);

				if (end < 0) {
					EmitComment(line, start, line.Length, isDoc, output);
					return state;
				}

				position = end + language.BlockCommentEnd!.Length;
				EmitComment(line, start, position, isDoc, output);
				state = LineState.Normal;
				continue;
			}

			if (language.HasPreprocessor && current == '#' && IsFirstOnLine(line, position)) {
				output.Add(new Token(start, line.Length - start, TokenKind.Preprocessor));
				return LineState.Normal;
			}

			if (language.HasVerbatimStrings && current == '@' && position + 1 < line.Length &&
				line[position + 1] == language.StringQuote) {
				position = ScanVerbatimString(line, position, output, ref state);
				continue;
			}

			if (current == language.StringQuote) {
				position = ScanQuoted(line, position, language.StringQuote, output);
				continue;
			}

			if (language.CharQuote is { } charQuote && current == charQuote) {
				position = ScanQuoted(line, position, charQuote, output);
				continue;
			}

			if (char.IsAsciiDigit(current) ||
				(current == '.' && position + 1 < line.Length && char.IsAsciiDigit(line[position + 1]))) {
				position = ScanNumber(line, position, output);
				continue;
			}

			if (char.IsLetter(current) || current == '_' || current == '$') {
				position = ScanIdentifier(line, position, language, output);
				continue;
			}

			if (IsPunctuation(current)) {
				output.Add(new Token(start, 1, TokenKind.Punctuation));
				position++;
				continue;
			}

			output.Add(new Token(start, 1, TokenKind.Operator));
			position++;
		}

		return state;
	}

	private static LineState TokenizeMarkup(ReadOnlySpan<char> line, LineState startState,
		LanguageDefinition language, List<Token> output) {
		int position = 0;
		LineState state = startState;

		if (state == LineState.BlockComment) {
			int commentEnd = IndexOf(line, 0, language.BlockCommentEnd);
			if (commentEnd < 0) {
				if (line.Length > 0) output.Add(new Token(0, line.Length, TokenKind.Comment));

				return LineState.BlockComment;
			}

			position = commentEnd + language.BlockCommentEnd!.Length;
			output.Add(new Token(0, position, TokenKind.Comment));
			state = LineState.Normal;
		}
		else if (state == LineState.MarkupTag) position = ScanTagBody(line, 0, output, ref state);

		while (position < line.Length) {
			if (line[position] != '<') {
				int contentStart = position;
				while (position < line.Length && line[position] != '<') position++;

				output.Add(new Token(contentStart, position - contentStart, TokenKind.Text));
				continue;
			}

			if (Matches(line, position, language.BlockCommentStart)) {
				int start = position;
				int commentEnd = IndexOf(line, position + language.BlockCommentStart!.Length,
					language.BlockCommentEnd);

				if (commentEnd < 0) {
					output.Add(new Token(start, line.Length - start, TokenKind.Comment));
					return LineState.BlockComment;
				}

				position = commentEnd + language.BlockCommentEnd!.Length;
				output.Add(new Token(start, position - start, TokenKind.Comment));
				continue;
			}

			position = ScanTagOpening(line, position, output, ref state);
		}

		return state;
	}

	private static int ScanTagOpening(ReadOnlySpan<char> line, int position, List<Token> output,
		ref LineState state) {
		int start = position;
		position++;

		while (position < line.Length && line[position] is '/' or '?' or '!') position++;

		output.Add(new Token(start, position - start, TokenKind.Punctuation));

		int nameStart = position;
		while (position < line.Length && IsMarkupNameChar(line[position])) position++;

		if (position > nameStart) output.Add(new Token(nameStart, position - nameStart, TokenKind.Tag));

		return ScanTagBody(line, position, output, ref state);
	}

	private static int ScanTagBody(ReadOnlySpan<char> line, int position, List<Token> output,
		ref LineState state) {
		while (position < line.Length) {
			char current = line[position];

			if (current is ' ' or '\t') {
				position++;
				continue;
			}

			if (current == '>') {
				output.Add(new Token(position, 1, TokenKind.Punctuation));
				state = LineState.Normal;
				return position + 1;
			}

			if (current is '/' or '?' && position + 1 < line.Length && line[position + 1] == '>') {
				output.Add(new Token(position, 2, TokenKind.Punctuation));
				state = LineState.Normal;
				return position + 2;
			}

			if (current is '"' or '\'') {
				int valueStart = position++;
				while (position < line.Length && line[position] != current) position++;

				if (position < line.Length) position++;

				output.Add(new Token(valueStart, position - valueStart, TokenKind.String));
				continue;
			}

			if (IsMarkupNameChar(current)) {
				int nameStart = position;
				while (position < line.Length && IsMarkupNameChar(line[position])) position++;

				output.Add(new Token(nameStart, position - nameStart, TokenKind.Attribute));
				continue;
			}

			output.Add(new Token(position, 1, TokenKind.Operator));
			position++;
		}

		state = LineState.MarkupTag;
		return position;
	}

	private static bool IsMarkupNameChar(char c) =>
		char.IsLetterOrDigit(c) || c is '_' or '-' or ':' or '.';

	private static void EmitComment(ReadOnlySpan<char> line, int start, int end, bool isDoc, List<Token> output) {
		if (isDoc) ScanDocComment(line[start..end], start, output);
		else output.Add(new Token(start, end - start, TokenKind.Comment));
	}

	private static int ContinueBlockComment(ReadOnlySpan<char> line, LanguageDefinition language,
		LineState entryState, List<Token> output, ref LineState state) {
		bool isDoc = entryState == LineState.DocBlockComment;
		int end = IndexOf(line, 0, language.BlockCommentEnd);

		if (end < 0) {
			if (line.Length > 0) EmitComment(line, 0, line.Length, isDoc, output);

			return line.Length;
		}

		int length = end + language.BlockCommentEnd!.Length;
		EmitComment(line, 0, length, isDoc, output);
		state = LineState.Normal;
		return length;
	}

	private static void ScanDocComment(ReadOnlySpan<char> line, int offset, List<Token> output) {
		int position = 0;
		int prose = 0;

		while (position < line.Length) {
			if (line[position] != '<') {
				position++;
				continue;
			}

			int close = line[position..].IndexOf('>');
			if (close < 0) break;

			close += position;

			if (position > prose) output.Add(new Token(offset + prose, position - prose, TokenKind.DocComment));

			ScanDocTag(line[position..(close + 1)], offset + position, output);

			position = close + 1;
			prose = position;
		}

		if (prose < line.Length) output.Add(new Token(offset + prose, line.Length - prose, TokenKind.DocComment));
	}

	private static void ScanDocTag(ReadOnlySpan<char> tag, int offset, List<Token> output) {
		int position = 1;

		while (position < tag.Length && (tag[position] == '/' || tag[position] == '!')) position++;

		output.Add(new Token(offset, position, TokenKind.DocComment));

		int nameStart = position;
		while (position < tag.Length && (char.IsLetterOrDigit(tag[position]) || tag[position] is '_' or '-' or ':')) {
			position++;
		}

		if (position > nameStart) output.Add(new Token(offset + nameStart, position - nameStart, TokenKind.Tag));

		while (position < tag.Length) {
			char current = tag[position];

			if (current == '"' || current == '\'') {
				int valueStart = position++;
				while (position < tag.Length && tag[position] != current) position++;

				if (position < tag.Length) position++;

				output.Add(new Token(offset + valueStart, position - valueStart, TokenKind.String));
				continue;
			}

			if (char.IsLetter(current) || current == '_') {
				int nameAt = position;
				while (position < tag.Length &&
					(char.IsLetterOrDigit(tag[position]) || tag[position] is '_' or '-' or ':')) position++;

				output.Add(new Token(offset + nameAt, position - nameAt, TokenKind.Attribute));
				continue;
			}

			output.Add(new Token(offset + position, 1, TokenKind.DocComment));
			position++;
		}
	}

	private static int ContinueVerbatimString(ReadOnlySpan<char> line, List<Token> output, ref LineState state) {
		for (int i = 0; i < line.Length; i++) {
			if (line[i] != '"') continue;

			if (i + 1 < line.Length && line[i + 1] == '"') {
				i++;
				continue;
			}

			output.Add(new Token(0, i + 1, TokenKind.String));
			state = LineState.Normal;
			return i + 1;
		}

		if (line.Length > 0) output.Add(new Token(0, line.Length, TokenKind.String));

		return line.Length;
	}

	private static int ScanVerbatimString(ReadOnlySpan<char> line, int position, List<Token> output,
		ref LineState state) {
		int start = position;
		position += 2;

		while (position < line.Length) {
			if (line[position] == '"') {
				if (position + 1 < line.Length && line[position + 1] == '"') {
					position += 2;
					continue;
				}

				position++;
				output.Add(new Token(start, position - start, TokenKind.String));
				return position;
			}

			position++;
		}

		output.Add(new Token(start, line.Length - start, TokenKind.String));
		state = LineState.MultiLineString;
		return line.Length;
	}

	private static int ScanQuoted(ReadOnlySpan<char> line, int position, char quote, List<Token> output) {
		int start = position;
		position++;

		while (position < line.Length) {
			char current = line[position];

			if (current == '\\' && position + 1 < line.Length) {
				position += 2;
				continue;
			}

			position++;

			if (current == quote) break;
		}

		output.Add(new Token(start, position - start, TokenKind.String));
		return position;
	}

	private static int ScanNumber(ReadOnlySpan<char> line, int position, List<Token> output) {
		int start = position;

		if (line[position] == '0' && position + 1 < line.Length &&
			(line[position + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')) {
			position += 2;
			while (position < line.Length && (char.IsAsciiLetterOrDigit(line[position]) || line[position] == '_')) {
				position++;
			}

			output.Add(new Token(start, position - start, TokenKind.Number));
			return position;
		}

		bool seenDot = false;
		while (position < line.Length) {
			char current = line[position];

			if (char.IsAsciiDigit(current) || current == '_') {
				position++;
				continue;
			}

			if (current == '.' && !seenDot && position + 1 < line.Length && char.IsAsciiDigit(line[position + 1])) {
				seenDot = true;
				position++;
				continue;
			}

			if (current is 'e' or 'E' && position + 1 < line.Length &&
				(char.IsAsciiDigit(line[position + 1]) || line[position + 1] is '+' or '-')) {
				position += 2;
				continue;
			}

			if (char.IsAsciiLetter(current)) {
				position++;
				continue;
			}

			break;
		}

		output.Add(new Token(start, position - start, TokenKind.Number));
		return position;
	}

	private static int ScanIdentifier(ReadOnlySpan<char> line, int position, LanguageDefinition language,
		List<Token> output) {
		int start = position;
		while (position < line.Length &&
			(char.IsLetterOrDigit(line[position]) || line[position] == '_' || line[position] == '$')) position++;

		ReadOnlySpan<char> word = line[start..position];
		TokenKind kind = language.Classify(word);

		if (kind == TokenKind.Type && FollowsMemberAccess(line, start) && !PrecedesCall(line, position)) {
			kind = TokenKind.Variable;
		}

		if (kind == TokenKind.Text || kind == TokenKind.Type) {
			int next = position;
			while (next < line.Length && line[next] == ' ') next++;

			if (next < line.Length && line[next] == '(') kind = TokenKind.Function;
		}

		output.Add(new Token(start, position - start, kind));
		return position;
	}

	private static bool FollowsMemberAccess(ReadOnlySpan<char> line, int start) {
		int index = start - 1;
		while (index >= 0 && line[index] is ' ' or '\t') index--;

		return index >= 0 && (line[index] == '.' || (line[index] == '>' && index > 0 && line[index - 1] == '-'));
	}

	private static bool PrecedesCall(ReadOnlySpan<char> line, int position) {
		while (position < line.Length && line[position] is ' ' or '\t') position++;

		return position < line.Length && (line[position] == '(' || line[position] == '<');
	}

	private static bool IsDocComment(ReadOnlySpan<char> line, int position, LanguageDefinition language) {
		int after = position + language.BlockCommentStart!.Length;
		return after < line.Length && line[after] == '*';
	}

	private static bool IsFirstOnLine(ReadOnlySpan<char> line, int position) {
		for (int i = 0; i < position; i++) {
			if (line[i] is not (' ' or '\t')) return false;
		}

		return true;
	}

	private static bool IsPunctuation(char c) =>
		c is '(' or ')' or '[' or ']' or '{' or '}' or ',' or ';' or '.' or ':';

	private static bool Matches(ReadOnlySpan<char> line, int position, string? pattern) {
		if (pattern is null || position + pattern.Length > line.Length) return false;

		return line.Slice(position, pattern.Length).SequenceEqual(pattern);
	}

	private static int IndexOf(ReadOnlySpan<char> line, int from, string? pattern) {
		if (pattern is null || from >= line.Length) return -1;

		int index = line[from..].IndexOf(pattern);
		return index < 0 ? -1 : index + from;
	}
}
