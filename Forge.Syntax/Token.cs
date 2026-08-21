namespace Forge.Syntax;

public enum TokenKind : byte
{
	Text,
	Keyword,
	ControlKeyword,
	Type,
	Function,
	Variable,
	Parameter,
	Constant,
	Number,
	String,
	StringEscape,
	Comment,
	DocComment,
	Operator,
	Punctuation,
	Preprocessor,
	Tag,
	Attribute,
	Invalid,
}

public readonly struct Token(int start, int length, TokenKind kind)
{
	public readonly int Start = start;
	public readonly int Length = length;
	public readonly TokenKind Kind = kind;

	public int End => Start + Length;

	public override string ToString() => $"{Kind} [{Start}..{End})";
}

public enum LineState : byte
{
	Normal = 0,
	BlockComment = 1,
	DocBlockComment = 2,
	MultiLineString = 3,
	RawString = 4,

	MarkupTag = 5,
}
