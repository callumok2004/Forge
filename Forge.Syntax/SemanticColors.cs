using Forge.Theming;

namespace Forge.Syntax;

public static class SemanticColors
{
	public static bool Enabled { get; set; }

	public static Color? For(string type) {
		if (!Enabled || type.Length == 0) return null;

		int space = type.LastIndexOf(' ');
		ReadOnlySpan<char> name = space < 0 ? type : type.AsSpan(space + 1);

		return Kind(name) is { } kind ? SyntaxColors.For(kind) : null;
	}

	private static TokenKind? Kind(ReadOnlySpan<char> name) => name switch {
		"namespace" or "class" or "struct" or "interface" or "enum" or "type" or "typeParameter"
			or "delegate" or "record" or "recordStruct" or "module" => TokenKind.Type,

		"property" or "field" or "variable" or "local" or "event" => TokenKind.Variable,

		"method" or "function" or "extensionMethod" => TokenKind.Function,

		"keyword" or "controlKeyword" => TokenKind.Keyword,

		"preprocessorKeyword" or "preprocessor" => TokenKind.Preprocessor,

		"string" or "verbatimString" => TokenKind.String,

		"number" => TokenKind.Number,

		"comment" => TokenKind.Comment,

		_ => null,
	};
}
