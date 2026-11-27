using System.Globalization;
using System.Text;

namespace Forge.Extensions.GLua.Parsing;

public sealed class LuaSyntaxException(string message) : Exception(message);

public sealed class LuaParser
{
	private const int Eof = 1;
	private const int StringLiteral = 2;
	private const int Keyword = 4;
	private const int Identifier = 8;
	private const int NumericLiteral = 16;
	private const int Punctuator = 32;
	private const int BooleanLiteral = 64;
	private const int NilLiteral = 128;
	private const int VarargLiteral = 256;

	private struct Token
	{
		public int Type;
		public string Value;
		public bool Bool;
		public int Line;
		public int LineStart;
		public int LastLine;
		public int LastLineStart;
		public int RangeStart;
		public int RangeEnd;
	}

	private readonly struct Marker(int line, int column)
	{
		public int Line { get; } = line;

		public int Column { get; } = column;
	}

	private readonly string _input;
	private readonly int _length;
	private int _index;
	private int _line = 1;
	private int _lineStart;
	private int _tokenStart;
	private Token _token;
	private Token _previous;
	private Token _lookahead;
	private readonly List<Marker> _locations = [];
	private readonly List<List<string>> _scopes = [[]];
	private int _scopeDepth;

	private LuaParser(string input) {
		if (input.Length > 0 && input[0] == '﻿') input = input[1..];

		if (input.StartsWith("#!", StringComparison.Ordinal)) {
			int end = 0;

			while (end < input.Length && input[end] is not ('\r' or '\n')) end++;

			input = string.Concat(new string(' ', end), input.AsSpan(end));
		}

		_input = input;
		_length = input.Length;
	}

	public static LuaNode Parse(string input) {
		LuaParser parser = new(input);
		parser._lookahead = parser.Lex();
		return parser.ParseChunk();
	}

	private char At(int index) => index < _length ? _input[index] : '\0';

	private LuaSyntaxException Raise(string message) => new($"[{_line}:{_index - _lineStart + 1}] {message}");

	private LuaSyntaxException Raise(in Token token, string message) => new($"[{token.Line}:{token.RangeStart - token.LineStart}] {message}");

	private Token Lex() {
		SkipWhiteSpace();

		while ((At(_index) == '-' && At(_index + 1) == '-') || (At(_index) == '/' && At(_index + 1) is '/' or '*')) {
			ScanComment(At(_index + 1) == '*' ? 2 : At(_index) == '/' ? 1 : 0);
			SkipWhiteSpace();
		}

		if (_index >= _length) return new Token { Type = Eof, Value = "<eof>", Line = _line, LineStart = _lineStart, LastLine = _line, LastLineStart = _lineStart, RangeStart = _index, RangeEnd = _index };

		char character = _input[_index];
		char next = At(_index + 1);

		_tokenStart = _index;

		if (IsIdentifierStart(character)) return ScanIdentifierOrKeyword();

		switch (character) {
			case '\'':
			case '"':
				return ScanStringLiteral();
			case >= '0' and <= '9':
				return ScanNumericLiteral();
			case '.':
				if (char.IsAsciiDigit(next)) return ScanNumericLiteral();

				if (next == '.') return At(_index + 2) == '.' ? ScanSimple(VarargLiteral, "...") : ScanSimple(Punctuator, "..");

				return ScanSimple(Punctuator, ".");
			case '=':
				return ScanSimple(Punctuator, next == '=' ? "==" : "=");
			case '>':
				return ScanSimple(Punctuator, next == '=' ? ">=" : ">");
			case '<':
				return ScanSimple(Punctuator, next == '=' ? "<=" : "<");
			case '!':
				if (next == '=') {
					_index += 2;
					return Simple(Punctuator, "~=", _tokenStart);
				}

				return ScanOperatorKeyword(1, "not");
			case '~':
				if (next == '=') return ScanSimple(Punctuator, "~=");

				break;
			case ':':
				return ScanSimple(Punctuator, next == ':' ? "::" : ":");
			case '[':
				if (next is '[' or '=') return ScanLongStringLiteral();

				return ScanSimple(Punctuator, "[");
			case '&':
				if (next == '&') return ScanOperatorKeyword(2, "and");

				break;
			case '|':
				if (next == '|') return ScanOperatorKeyword(2, "or");

				break;
			case '*' or '^' or '%' or ',' or '{' or '}' or ']' or '(' or ')' or ';' or '#' or '-' or '+' or '/':
				return ScanSimple(Punctuator, character.ToString());
		}

		throw Raise($"unexpected symbol '{character}' near '{_lookahead.Value}'");
	}

	private bool ConsumeEndOfLine() {
		char character = At(_index);

		if (character is not ('\n' or '\r')) return false;

		char peek = At(_index + 1);

		if (character == '\n' && peek == '\r') ++_index;

		if (character == '\r' && peek == '\n') ++_index;

		++_line;
		_lineStart = ++_index;
		return true;
	}

	private void SkipWhiteSpace() {
		while (_index < _length) {
			char character = _input[_index];

			if (character is '\t' or ' ' or '\v' or '\f') ++_index;
			else if (!ConsumeEndOfLine()) break;
		}
	}

	private Token Simple(int type, string value, int start) => new() { Type = type, Value = value, Line = _line, LineStart = _lineStart, LastLine = _line, LastLineStart = _lineStart, RangeStart = start, RangeEnd = _index };

	private Token ScanSimple(int type, string value) {
		_index += value.Length;
		return Simple(type, value, _tokenStart);
	}

	private Token ScanOperatorKeyword(int size, string name) {
		_index += size;
		return Simple(Keyword, name, _index - size);
	}

	private Token ScanIdentifierOrKeyword() {
		while (IsIdentifierPart(At(++_index))) {
		}

		string value = _input[_tokenStart.._index];
		Token token = Simple(Identifier, value, _tokenStart);

		if (IsKeyword(value)) token.Type = Keyword;
		else if (value is "true" or "false") {
			token.Type = BooleanLiteral;
			token.Bool = value == "true";
		}
		else if (value == "nil") token.Type = NilLiteral;

		return token;
	}

	private Token ScanStringLiteral() {
		char delimiter = _input[_index++];
		int beginLine = _line;
		int beginLineStart = _lineStart;
		int stringStart = _index;
		StringBuilder text = new();

		while (_index < _length) {
			char character = _input[_index++];

			if (delimiter == character) break;

			if (character == '\\') {
				text.Append(_input, stringStart, _index - 1 - stringStart).Append(ReadEscapeSequence());
				stringStart = _index;
			}
			else if (_index >= _length || character is '\n' or '\r') {
				text.Append(_input, stringStart, _index - 1 - stringStart);
				throw Raise($"unfinished string near '{text}{character}'");
			}
		}

		text.Append(_input, stringStart, Math.Max(0, _index - 1 - stringStart));

		return new Token { Type = StringLiteral, Value = text.ToString(), Line = beginLine, LineStart = beginLineStart, LastLine = _line, LastLineStart = _lineStart, RangeStart = _tokenStart, RangeEnd = _index };
	}

	private Token ScanLongStringLiteral() {
		int beginLine = _line;
		int beginLineStart = _lineStart;

		if (ReadLongString(isComment: false) is not { } text) throw Raise(_token, $"'[' expected near '{_token.Value}'");

		return new Token { Type = StringLiteral, Value = text, Line = beginLine, LineStart = beginLineStart, LastLine = _line, LastLineStart = _lineStart, RangeStart = _tokenStart, RangeEnd = _index };
	}

	private Token ScanNumericLiteral() {
		if (At(_index) == '0' && At(_index + 1) is 'x' or 'X') ReadHexLiteral();
		else ReadDecimalLiteral();

		return Simple(NumericLiteral, _input[_tokenStart.._index], _tokenStart);
	}

	private void ReadHexLiteral() {
		_index += 2;

		if (!char.IsAsciiHexDigit(At(_index))) throw Raise($"malformed number near '{_input[_tokenStart.._index]}'");

		while (char.IsAsciiHexDigit(At(_index))) ++_index;

		if (At(_index) == '.') {
			++_index;

			while (char.IsAsciiHexDigit(At(_index))) ++_index;
		}

		if (At(_index) is 'p' or 'P') {
			++_index;

			if (At(_index) is '+' or '-') ++_index;

			if (!char.IsAsciiDigit(At(_index))) throw Raise($"malformed number near '{_input[_tokenStart.._index]}'");

			while (char.IsAsciiDigit(At(_index))) ++_index;
		}
	}

	private void ReadDecimalLiteral() {
		while (char.IsAsciiDigit(At(_index))) ++_index;

		if (At(_index) == '.') {
			++_index;

			while (char.IsAsciiDigit(At(_index))) ++_index;
		}

		if (At(_index) is 'e' or 'E') {
			++_index;

			if (At(_index) is '+' or '-') ++_index;

			if (!char.IsAsciiDigit(At(_index))) throw Raise($"malformed number near '{_input[_tokenStart.._index]}'");

			while (char.IsAsciiDigit(At(_index))) ++_index;
		}
	}

	private string ReadEscapeSequence() {
		int sequenceStart = _index;

		switch (At(_index)) {
			case 'a': ++_index; return "\a";
			case 'n': ++_index; return "\n";
			case 'r': ++_index; return "\r";
			case 't': ++_index; return "\t";
			case 'v': ++_index; return "\v";
			case 'b': ++_index; return "\b";
			case 'f': ++_index; return "\f";
			case '\r':
			case '\n':
				ConsumeEndOfLine();
				return "\n";
			case >= '0' and <= '9':
				while (char.IsAsciiDigit(At(_index)) && _index - sequenceStart < 3) ++_index;

				int value = int.Parse(_input.AsSpan(sequenceStart, _index - sequenceStart), CultureInfo.InvariantCulture);

				if (value > 255) throw Raise($"decimal escape too large near '\\{value}'");

				return ((char)value).ToString();
			case 'z':
				++_index;
				SkipWhiteSpace();
				return string.Empty;
			case 'x':
				if (char.IsAsciiHexDigit(At(_index + 1)) && char.IsAsciiHexDigit(At(_index + 2))) {
					_index += 3;
					return ((char)int.Parse(_input.AsSpan(sequenceStart + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
				}

				throw Raise("hexadecimal digit expected");
			case '\\' or '"' or '\'':
				return _input[_index++].ToString();
			default:
				throw Raise("invalid escape sequence");
		}
	}

	private void ScanComment(int style) {
		_tokenStart = _index;
		_index += 2;

		bool isLong = false;

		if (style == 2) {
			ReadCStyleComment();
			isLong = true;
		}
		else if (style == 0 && At(_index) == '[') isLong = ReadLongString(isComment: true) is not null;

		if (isLong) return;

		while (_index < _length && _input[_index] is not ('\n' or '\r')) ++_index;
	}

	private string? ReadLongString(bool isComment) {
		int level = 0;
		int firstLine = _line;

		++_index;

		while (At(_index + level) == '=') ++level;

		if (At(_index + level) != '[') return null;

		_index += level + 1;

		if (At(_index) is '\n' or '\r') ConsumeEndOfLine();

		int stringStart = _index;

		while (_index < _length) {
			if (At(_index) is '\n' or '\r') ConsumeEndOfLine();

			char character = At(_index++);

			if (character != ']') continue;

			bool terminator = true;

			for (int i = 0; i < level; ++i) {
				if (At(_index + i) != '=') terminator = false;
			}

			if (At(_index + level) != ']') terminator = false;

			if (!terminator) continue;

			string content = _input[stringStart..(_index - 1)];
			_index += level + 1;
			return content;
		}

		throw Raise(isComment ? $"unfinished long comment (starting at line {firstLine}) near '<eof>'" : $"unfinished long string (starting at line {firstLine}) near '<eof>'");
	}

	private void ReadCStyleComment() {
		if (At(_index) is '\n' or '\r') ConsumeEndOfLine();

		while (_index < _length) {
			if (At(_index) is '\n' or '\r') ConsumeEndOfLine();

			char character = At(_index++);

			if (character == '*' && At(_index) == '/') break;
		}

		_index += 1;
	}

	private void Next() {
		_previous = _token;
		_token = _lookahead;
		_lookahead = Lex();
	}

	private bool Consume(string value) {
		if (!IsValue(_token, value)) return false;

		Next();
		return true;
	}

	private void Expect(string value) {
		if (IsValue(_token, value)) Next();
		else throw Raise(_token, $"'{value}' expected near '{_token.Value}'");
	}

	private static bool IsValue(in Token token, string value) =>
		token.Type is not (StringLiteral or Eof or NumericLiteral) && token.Value == value;

	private static bool IsIdentifierStart(char character) => char.IsAsciiLetter(character) || character == '_' || character >= 128;

	private static bool IsIdentifierPart(char character) => char.IsAsciiLetterOrDigit(character) || character == '_' || character >= 128;

	private static bool IsKeyword(string id) => id switch {
		"do" or "if" or "in" or "or" or "and" or "end" or "for" or "not" or "else" or "then" or "goto" or "break" or "local" or "until" or "while" or "elseif" or "repeat" or "return" or "function" or "continue" => true,
		_ => false,
	};

	private static bool IsUnary(in Token token) =>
		(token.Type == Punctuator && token.Value is "#" or "-" or "~") || (token.Type == Keyword && token.Value is "not" or "!");

	private static bool IsCallExpression(LuaNode expression) =>
		expression.Type is LuaNodeType.CallExpression or LuaNodeType.TableCallExpression or LuaNodeType.StringCallExpression;

	private static bool IsBlockFollow(in Token token) =>
		token.Type == Eof || (token.Type == Keyword && token.Value is "else" or "elseif" or "end" or "until");

	private void CreateScope() {
		_scopes.Add([.. _scopes[_scopeDepth++]]);
	}

	private void DestroyScope() {
		_scopes.RemoveAt(_scopes.Count - 1);
		_scopeDepth--;
	}

	private void ScopeIdentifierName(string name) {
		List<string> scope = _scopes[_scopeDepth];

		if (!scope.Contains(name)) scope.Add(name);
	}

	private void ScopeIdentifier(LuaNode node) {
		ScopeIdentifierName(node.Name!);
		node.IsLocal = true;
	}

	private bool ScopeHasName(string name) => _scopes[_scopeDepth].Contains(name);

	private Marker CreateMarker() => new(_token.Line, _token.RangeStart - _token.LineStart);

	private void MarkLocation() => _locations.Add(CreateMarker());

	private void PushLocation(Marker marker) => _locations.Add(marker);

	private LuaNode Finish(LuaNode node) {
		Marker marker = _locations[^1];
		_locations.RemoveAt(_locations.Count - 1);
		node.StartLine = marker.Line;
		node.StartColumn = marker.Column;
		node.EndLine = _previous.LastLine;
		node.EndColumn = _previous.RangeEnd - _previous.LastLineStart;
		return node;
	}

	private LuaNode ParseChunk() {
		Next();
		MarkLocation();
		CreateScope();
		List<LuaNode> body = ParseBlock();
		DestroyScope();

		if (_token.Type != Eof) throw Unexpected(_token);

		if (body.Count == 0) _previous = _token;

		return Finish(new LuaNode(LuaNodeType.Chunk) { Body = body });
	}

	private LuaSyntaxException Unexpected(in Token found) => Raise(found, $"unexpected '{found.Value}' near '{_lookahead.Value}'");

	private List<LuaNode> ParseBlock() {
		List<LuaNode> block = [];

		while (!IsBlockFollow(_token)) {
			if (IsValue(_token, "return")) {
				block.Add(ParseStatement()!);
				break;
			}

			LuaNode? statement = ParseStatement();
			Consume(";");

			if (statement is not null) block.Add(statement);
		}

		return block;
	}

	private LuaNode? ParseStatement() {
		MarkLocation();

		if (_token.Type == Keyword) {
			switch (_token.Value) {
				case "local": Next(); return ParseLocalStatement();
				case "if": Next(); return ParseIfStatement();
				case "return": Next(); return ParseReturnStatement();
				case "function":
					Next();
					return ParseFunctionDeclaration(ParseFunctionName(), isLocal: false);
				case "while": Next(); return ParseWhileStatement();
				case "for": Next(); return ParseForStatement();
				case "repeat": Next(); return ParseRepeatStatement();
				case "break": Next(); return Finish(new LuaNode(LuaNodeType.BreakStatement));
				case "continue": Next(); return Finish(new LuaNode(LuaNodeType.ContinueStatement));
				case "do": Next(); return ParseDoStatement();
				case "goto": Next(); return Finish(new LuaNode(LuaNodeType.GotoStatement) { Label = ParseIdentifier() });
			}
		}

		if (_token.Type == Punctuator && Consume("::")) return ParseLabelStatement();

		_locations.RemoveAt(_locations.Count - 1);

		if (Consume(";")) return null;

		return ParseAssignmentOrCallStatement();
	}

	private LuaNode ParseLabelStatement() {
		string name = _token.Value;
		LuaNode label = ParseIdentifier();

		ScopeIdentifierName("::" + name + "::");
		label.IsLocal = true;

		Expect("::");
		return Finish(new LuaNode(LuaNodeType.LabelStatement) { Label = label });
	}

	private LuaNode ParseDoStatement() {
		CreateScope();
		List<LuaNode> body = ParseBlock();
		DestroyScope();
		Expect("end");
		return Finish(new LuaNode(LuaNodeType.DoStatement) { Body = body });
	}

	private LuaNode ParseWhileStatement() {
		LuaNode condition = ParseExpectedExpression();
		Expect("do");
		CreateScope();
		List<LuaNode> body = ParseBlock();
		DestroyScope();
		Expect("end");
		return Finish(new LuaNode(LuaNodeType.WhileStatement) { Condition = condition, Body = body });
	}

	private LuaNode ParseRepeatStatement() {
		CreateScope();
		List<LuaNode> body = ParseBlock();
		Expect("until");
		LuaNode condition = ParseExpectedExpression();
		DestroyScope();
		return Finish(new LuaNode(LuaNodeType.RepeatStatement) { Condition = condition, Body = body });
	}

	private LuaNode ParseReturnStatement() {
		List<LuaNode> expressions = [];

		if (!IsValue(_token, "end")) {
			if (ParseExpression() is { } expression) expressions.Add(expression);

			while (Consume(",")) expressions.Add(ParseExpectedExpression());

			Consume(";");
		}

		return Finish(new LuaNode(LuaNodeType.ReturnStatement) { Arguments = expressions });
	}

	private LuaNode ParseIfStatement() {
		List<LuaNode> clauses = [];
		Marker marker = _locations[^1];
		_locations.Add(marker);

		LuaNode condition = ParseExpectedExpression();
		Expect("then");
		CreateScope();
		List<LuaNode> body = ParseBlock();
		DestroyScope();
		clauses.Add(Finish(new LuaNode(LuaNodeType.IfClause) { Condition = condition, Body = body }));

		marker = CreateMarker();

		while (Consume("elseif")) {
			PushLocation(marker);
			condition = ParseExpectedExpression();
			Expect("then");
			CreateScope();
			body = ParseBlock();
			DestroyScope();
			clauses.Add(Finish(new LuaNode(LuaNodeType.ElseifClause) { Condition = condition, Body = body }));
			marker = CreateMarker();
		}

		if (Consume("else")) {
			_locations.Add(new Marker(_previous.Line, _previous.RangeStart - _previous.LineStart));
			CreateScope();
			body = ParseBlock();
			DestroyScope();
			clauses.Add(Finish(new LuaNode(LuaNodeType.ElseClause) { Body = body }));
		}

		Expect("end");
		return Finish(new LuaNode(LuaNodeType.IfStatement) { Clauses = clauses });
	}

	private LuaNode ParseForStatement() {
		LuaNode variable = ParseIdentifier();

		CreateScope();
		ScopeIdentifier(variable);

		if (Consume("=")) {
			LuaNode start = ParseExpectedExpression();
			Expect(",");
			LuaNode end = ParseExpectedExpression();
			LuaNode? step = Consume(",") ? ParseExpectedExpression() : null;

			Expect("do");
			List<LuaNode> numericBody = ParseBlock();
			Expect("end");
			DestroyScope();

			return Finish(new LuaNode(LuaNodeType.ForNumericStatement) { Variable = variable, Start = start, End = end, Step = step, Body = numericBody });
		}

		List<LuaNode> variables = [variable];

		while (Consume(",")) {
			variable = ParseIdentifier();
			ScopeIdentifier(variable);
			variables.Add(variable);
		}

		Expect("in");
		List<LuaNode> iterators = [];

		do iterators.Add(ParseExpectedExpression());
		while (Consume(","));

		Expect("do");
		List<LuaNode> body = ParseBlock();
		Expect("end");
		DestroyScope();

		return Finish(new LuaNode(LuaNodeType.ForGenericStatement) { Variables = variables, Iterators = iterators, Body = body });
	}

	private LuaNode ParseLocalStatement() {
		if (_token.Type == Identifier) {
			List<LuaNode> variables = [];
			List<LuaNode> init = [];

			do variables.Add(ParseIdentifier());
			while (Consume(","));

			if (Consume("=")) {
				do init.Add(ParseExpectedExpression());
				while (Consume(","));
			}

			foreach (LuaNode variable in variables) ScopeIdentifier(variable);

			return Finish(new LuaNode(LuaNodeType.LocalStatement) { Variables = variables, Init = init });
		}

		if (!Consume("function")) throw Raise(_token, $"<name> expected near '{_token.Value}'");

		LuaNode name = ParseIdentifier();
		ScopeIdentifier(name);
		CreateScope();
		return ParseFunctionDeclaration(name, isLocal: true);
	}

	private static void ValidateVar(LuaNode node, in Token token) {
		if (node.InParens || node.Type is not (LuaNodeType.Identifier or LuaNodeType.MemberExpression or LuaNodeType.IndexExpression)) throw new LuaSyntaxException($"[{token.Line}:{token.RangeStart - token.LineStart}] invalid left-hand side of assignment near '{token.Value}'");
	}

	private LuaNode ParseAssignmentOrCallStatement() {
		Token previous = _token;
		Marker marker = CreateMarker();
		LuaNode? expression = ParsePrefixExpression();

		if (expression is null) throw Unexpected(_token);

		if (IsValue(_token, ",") || IsValue(_token, "=")) {
			List<LuaNode> variables = [expression];
			List<LuaNode> init = [];

			ValidateVar(expression, _token);

			while (Consume(",")) {
				LuaNode exp = ParsePrefixExpression() ?? throw Raise(_token, $"<expression> expected near '{_token.Value}'");
				ValidateVar(exp, _token);
				variables.Add(exp);
			}

			Expect("=");

			do init.Add(ParseExpectedExpression());
			while (Consume(","));

			PushLocation(marker);
			return Finish(new LuaNode(LuaNodeType.AssignmentStatement) { Variables = variables, Init = init });
		}

		if (IsCallExpression(expression)) {
			PushLocation(marker);
			return Finish(new LuaNode(LuaNodeType.CallStatement) { Expression = expression });
		}

		throw Unexpected(previous);
	}

	private LuaNode ParseIdentifier() {
		MarkLocation();
		string identifier = _token.Value;

		if (_token.Type != Identifier) throw Raise(_token, $"<name> expected near '{_token.Value}'");

		Next();
		return Finish(new LuaNode(LuaNodeType.Identifier) { Name = identifier });
	}

	private LuaNode ParseFunctionDeclaration(LuaNode? name, bool isLocal) {
		List<LuaNode> parameters = [];
		Expect("(");

		if (!Consume(")")) {
			while (true) {
				if (_token.Type == Identifier) {
					LuaNode parameter = ParseIdentifier();
					ScopeIdentifier(parameter);
					parameters.Add(parameter);

					if (Consume(",")) continue;

					if (Consume(")")) break;
				}
				else if (_token.Type == VarargLiteral) {
					parameters.Add(ParsePrimaryExpression()!);
					Expect(")");
					break;
				}
				else throw Raise(_token, $"<name> or '...' expected near '{_token.Value}'");
			}
		}

		List<LuaNode> body = ParseBlock();
		Expect("end");
		DestroyScope();

		return Finish(new LuaNode(LuaNodeType.FunctionDeclaration) { Identifier = name, IsLocal = isLocal, Parameters = parameters, Body = body });
	}

	private LuaNode ParseFunctionName() {
		Marker marker = CreateMarker();
		LuaNode root = ParseIdentifier();

		root.IsLocal = ScopeHasName(root.Name!);
		CreateScope();

		LuaNode name = root;

		while (Consume(".")) {
			PushLocation(marker);
			LuaNode member = ParseIdentifier();
			name = Finish(new LuaNode(LuaNodeType.MemberExpression) { Base = name, Indexer = '.', Identifier = member });
		}

		if (Consume(":")) {
			PushLocation(marker);
			LuaNode member = ParseIdentifier();
			name = Finish(new LuaNode(LuaNodeType.MemberExpression) { Base = name, Indexer = ':', Identifier = member });
			ScopeIdentifierName("self");
		}

		return name;
	}

	private LuaNode ParseTableConstructor() {
		List<LuaNode> fields = [];

		while (true) {
			MarkLocation();

			if (_token.Type == Punctuator && Consume("[")) {
				LuaNode key = ParseExpectedExpression();
				Expect("]");
				Expect("=");
				LuaNode value = ParseExpectedExpression();
				fields.Add(Finish(new LuaNode(LuaNodeType.TableKey) { Key = key, Value = value }));
			}
			else if (_token.Type == Identifier) {
				if (IsValue(_lookahead, "=")) {
					LuaNode key = ParseIdentifier();
					Next();
					LuaNode value = ParseExpectedExpression();
					fields.Add(Finish(new LuaNode(LuaNodeType.TableKeyString) { Key = key, Value = value }));
				}
				else fields.Add(Finish(new LuaNode(LuaNodeType.TableValue) { Value = ParseExpectedExpression() }));
			}
			else {
				if (ParseExpression() is not { } value) {
					_locations.RemoveAt(_locations.Count - 1);
					break;
				}

				fields.Add(Finish(new LuaNode(LuaNodeType.TableValue) { Value = value }));
			}

			if (IsValue(_token, ",") || IsValue(_token, ";")) {
				Next();
				continue;
			}

			break;
		}

		Expect("}");
		return Finish(new LuaNode(LuaNodeType.TableConstructorExpression) { Fields = fields });
	}

	private LuaNode? ParseExpression() => ParseSubExpression(0);

	private LuaNode ParseExpectedExpression() =>
		ParseExpression() ?? throw Raise(_token, $"<expression> expected near '{_token.Value}'");

	private static int BinaryPrecedence(string operation) => operation switch {
		"^" => 12,
		"*" or "/" or "%" or "//" => 10,
		"+" or "-" => 9,
		"&" => 6,
		"~" => 5,
		"|" => 4,
		"<" or ">" or "<=" or ">=" => 3,
		".." => 8,
		"<<" or ">>" => 7,
		"==" or "~=" or "!=" => 3,
		"or" or "||" => 1,
		"and" or "&&" => 2,
		_ => 0,
	};

	private LuaNode? ParseSubExpression(int minPrecedence) {
		string operation = _token.Value;
		Marker marker = CreateMarker();
		LuaNode? expression = null;

		if (IsUnary(_token)) {
			MarkLocation();
			Next();
			LuaNode argument = ParseSubExpression(10) ?? throw Raise(_token, $"<expression> expected near '{_token.Value}'");
			expression = Finish(new LuaNode(LuaNodeType.UnaryExpression) { Operator = operation, Argument = argument });
		}

		expression ??= ParsePrimaryExpression() ?? ParsePrefixExpression();

		if (expression is null) return null;

		while (true) {
			operation = _token.Value;

			int precedence = _token.Type is Punctuator or Keyword ? BinaryPrecedence(operation) : 0;

			if (precedence == 0 || precedence <= minPrecedence) break;

			if (operation is "^" or "..") precedence--;

			Next();
			LuaNode right = ParseSubExpression(precedence) ?? throw Raise(_token, $"<expression> expected near '{_token.Value}'");
			_locations.Add(marker);
			expression = Finish(new LuaNode(operation is "and" or "or" ? LuaNodeType.LogicalExpression : LuaNodeType.BinaryExpression) { Operator = operation, Left = expression, Right = right });
		}

		return expression;
	}

	private LuaNode? ParsePrefixExpression() {
		Marker marker = CreateMarker();
		LuaNode result;

		if (_token.Type == Identifier) {
			string name = _token.Value;
			result = ParseIdentifier();
			result.IsLocal = ScopeHasName(name);
		}
		else if (Consume("(")) {
			result = ParseExpectedExpression();
			Expect(")");
			result.InParens = true;
		}
		else return null;

		while (true) {
			if (_token.Type == Punctuator) {
				switch (_token.Value) {
					case "[":
						PushLocation(marker);
						Next();
						LuaNode index = ParseExpectedExpression();
						Expect("]");
						result = Finish(new LuaNode(LuaNodeType.IndexExpression) { Base = result, Index = index });
						break;
					case ".":
						PushLocation(marker);
						Next();
						LuaNode member = ParseIdentifier();
						result = Finish(new LuaNode(LuaNodeType.MemberExpression) { Base = result, Indexer = '.', Identifier = member });
						break;
					case ":":
						PushLocation(marker);
						Next();
						LuaNode method = ParseIdentifier();
						result = Finish(new LuaNode(LuaNodeType.MemberExpression) { Base = result, Indexer = ':', Identifier = method });
						PushLocation(marker);
						result = ParseCallExpression(result);
						break;
					case "(":
					case "{":
						PushLocation(marker);
						result = ParseCallExpression(result);
						break;
					default:
						return result;
				}
			}
			else if (_token.Type == StringLiteral) {
				PushLocation(marker);
				result = ParseCallExpression(result);
			}
			else break;
		}

		return result;
	}

	private LuaNode ParseCallExpression(LuaNode callee) {
		if (_token.Type == Punctuator) {
			switch (_token.Value) {
				case "(":
					Next();
					List<LuaNode> expressions = [];

					if (ParseExpression() is { } first) expressions.Add(first);

					while (Consume(",")) expressions.Add(ParseExpectedExpression());

					Expect(")");
					return Finish(new LuaNode(LuaNodeType.CallExpression) { Base = callee, Arguments = expressions });
				case "{":
					MarkLocation();
					Next();
					LuaNode table = ParseTableConstructor();
					return Finish(new LuaNode(LuaNodeType.TableCallExpression) { Base = callee, Argument = table });
			}
		}
		else if (_token.Type == StringLiteral) return Finish(new LuaNode(LuaNodeType.StringCallExpression) { Base = callee, Argument = ParsePrimaryExpression() });

		throw Raise(_token, $"function arguments expected near '{_token.Value}'");
	}

	private LuaNode? ParsePrimaryExpression() {
		int type = _token.Type;
		Marker marker = CreateMarker();

		if ((type & (StringLiteral | NumericLiteral | BooleanLiteral | NilLiteral | VarargLiteral)) != 0) {
			PushLocation(marker);
			string raw = _input[_token.RangeStart.._token.RangeEnd];
			string value = _token.Value;
			Next();

			return Finish(new LuaNode(type switch {
				StringLiteral => LuaNodeType.StringLiteral,
				NumericLiteral => LuaNodeType.NumericLiteral,
				BooleanLiteral => LuaNodeType.BooleanLiteral,
				NilLiteral => LuaNodeType.NilLiteral,
				_ => LuaNodeType.VarargLiteral,
			}) { Text = type == StringLiteral ? value : null, Raw = raw });
		}

		if (type == Keyword && _token.Value == "function") {
			PushLocation(marker);
			Next();
			CreateScope();
			return ParseFunctionDeclaration(null, isLocal: false);
		}

		if (Consume("{")) {
			PushLocation(marker);
			return ParseTableConstructor();
		}

		return null;
	}
}
