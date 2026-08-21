namespace Forge.Syntax;

public sealed class LanguageDefinition(
	string name,
	string[] extensions,
	string[] keywords,
	string[] controlKeywords,
	string[] types,
	string[] constants,
	string? lineComment = "//",
	string? blockCommentStart = "/*",
	string? blockCommentEnd = "*/",
	bool hasPreprocessor = false,
	bool hasVerbatimStrings = false,
	char stringQuote = '"',
	char? charQuote = '\'',
	TokenKind builtInTypeKind = TokenKind.Type,
	bool isMarkup = false,
	string? grammarId = null)
{
	private readonly HashSet<string> _keywords = new HashSet<string>(keywords, StringComparer.Ordinal);
	private readonly HashSet<string> _controlKeywords = new HashSet<string>(controlKeywords, StringComparer.Ordinal);
	private readonly HashSet<string> _types = new HashSet<string>(types, StringComparer.Ordinal);
	private readonly HashSet<string> _constants = new HashSet<string>(constants, StringComparer.Ordinal);

	public string Name { get; } = name;

	public string? GrammarId { get; } = grammarId;

	public string[] Extensions { get; } = extensions;

	public string? LineComment { get; } = lineComment;

	public string? BlockCommentStart { get; } = blockCommentStart;

	public string? BlockCommentEnd { get; } = blockCommentEnd;

	public bool HasPreprocessor { get; } = hasPreprocessor;

	public bool HasVerbatimStrings { get; } = hasVerbatimStrings;

	public char StringQuote { get; } = stringQuote;

	public char? CharQuote { get; } = charQuote;

	public TokenKind BuiltInTypeKind { get; } = builtInTypeKind;

	public bool IsMarkup { get; } = isMarkup;

	public TokenKind Classify(ReadOnlySpan<char> word) {
		if (_controlKeywords.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word)) {
			return TokenKind.ControlKeyword;
		}

		if (_keywords.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word)) return TokenKind.Keyword;

		if (_types.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word)) return BuiltInTypeKind;

		if (_constants.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word)) return TokenKind.Constant;

		return word.Length > 1 && char.IsUpper(word[0]) ? TokenKind.Type : TokenKind.Text;
	}
}
