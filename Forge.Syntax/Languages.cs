namespace Forge.Syntax;

public static class Languages
{
	public static LanguageDefinition CSharp { get; } = new(
		"C#",
		[".cs", ".csx"],
		keywords:
		[
			"abstract", "as", "base", "checked", "class", "const", "delegate", "enum", "event",
			"explicit", "extern", "fixed", "get", "implicit", "in", "init", "interface", "internal",
			"is", "lock", "namespace", "new", "operator", "out", "override", "params", "partial",
			"private", "protected", "public", "readonly", "record", "ref", "required", "sealed",
			"set", "sizeof", "stackalloc", "static", "struct", "typeof", "unchecked", "unsafe",
			"using", "virtual", "volatile", "where", "with", "file", "scoped", "global", "value",
			"add", "remove", "async", "await", "nameof", "when",
		],
		controlKeywords:
		[
			"break", "case", "catch", "continue", "default", "do", "else", "finally", "for",
			"foreach", "goto", "if", "return", "switch", "throw", "try", "while", "yield",
		],
		types:
		[
			"bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long",
			"nint", "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort",
			"var", "void",
		],
		constants: ["true", "false", "null", "this"],
		hasPreprocessor: true,
		hasVerbatimStrings: true,
		builtInTypeKind: TokenKind.Keyword,
		grammarId: "csharp");

	public static LanguageDefinition JavaScript { get; } = new(
		"JavaScript",
		[".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts"],
		keywords:
		[
			"as", "async", "await", "class", "const", "constructor", "declare", "delete", "enum",
			"export", "extends", "from", "function", "get", "implements", "import", "in",
			"instanceof", "interface", "let", "namespace", "new", "of", "private", "protected",
			"public", "readonly", "set", "static", "super", "type", "typeof", "var", "void",
			"satisfies", "keyof", "infer", "abstract", "override",
		],
		controlKeywords:
		[
			"break", "case", "catch", "continue", "default", "do", "else", "finally", "for", "if",
			"return", "switch", "throw", "try", "while", "yield",
		],
		types:
		[
			"any", "bigint", "boolean", "never", "number", "object", "string", "symbol",
			"unknown", "Array", "Promise", "Record", "Map", "Set",
		],
		constants: ["true", "false", "null", "undefined", "this", "NaN", "Infinity"],
		builtInTypeKind: TokenKind.Variable);

	public static LanguageDefinition Json { get; } = new(
		"JSON",
		[".json", ".jsonc", ".slnx", ".webmanifest"],
		keywords: [],
		controlKeywords: [],
		types: [],
		constants: ["true", "false", "null"],
		lineComment: "//",
		blockCommentStart: "/*",
		blockCommentEnd: "*/",
		charQuote: null);

	public static LanguageDefinition Xml { get; } = new(
		"XML",
		[".xml", ".csproj", ".props", ".targets", ".config", ".resx", ".manifest", ".html", ".htm", ".xaml"],
		keywords: [],
		controlKeywords: [],
		types: [],
		constants: [],
		lineComment: null,
		blockCommentStart: "<!--",
		blockCommentEnd: "-->",
		charQuote: null,
		isMarkup: true);

	public static LanguageDefinition Shell { get; } = new(
		"Shell",
		[".sh", ".bash", ".zsh", ".ps1", ".psm1", ".cmd", ".bat"],
		keywords: ["function", "local", "export", "param", "begin", "process", "end"],
		controlKeywords: ["if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "return"],
		types: [],
		constants: ["true", "false", "null"],
		lineComment: "#",
		blockCommentStart: null,
		blockCommentEnd: null);

	public static LanguageDefinition Ini { get; } = new(
		"Config",
		[".ini", ".editorconfig", ".gitconfig", ".toml", ".gitignore", ".gitattributes"],
		keywords: [],
		controlKeywords: [],
		types: [],
		constants: ["true", "false"],
		lineComment: "#",
		blockCommentStart: null,
		blockCommentEnd: null);

	public static LanguageDefinition PlainText { get; } = new(
		"Plain Text",
		[],
		keywords: [],
		controlKeywords: [],
		types: [],
		constants: [],
		lineComment: null,
		blockCommentStart: null,
		blockCommentEnd: null,
		charQuote: null);

	private static readonly Dictionary<string, LanguageDefinition> ByExtension = Build();
	private static readonly Dictionary<string, LanguageDefinition> FromGrammars = [with(StringComparer.OrdinalIgnoreCase)];
	private static readonly Lock GrammarSync = new();

	public static LanguageDefinition ForPath(string? path) {
		if (string.IsNullOrEmpty(path)) return PlainText;

		string extension = Path.GetExtension(path);
		if (extension.Length == 0) extension = Path.GetFileName(path);

		if (ByExtension.TryGetValue(extension, out LanguageDefinition? language)) return language;

		return GrammarIndex.ForPath(path) is { } entry ? FromGrammar(entry) : PlainText;
	}

	private static LanguageDefinition FromGrammar(GrammarEntry entry) {
		lock (GrammarSync) {
			if (FromGrammars.TryGetValue(entry.Id, out LanguageDefinition? cached)) return cached;

			LanguageDefinition language = new(
				entry.Name,
				entry.Extensions,
				keywords: [],
				controlKeywords: [],
				types: [],
				constants: [],
				lineComment: entry.LineComment,
				blockCommentStart: entry.BlockCommentStart,
				blockCommentEnd: entry.BlockCommentEnd,
				grammarId: entry.Id);

			FromGrammars[entry.Id] = language;
			return language;
		}
	}

	private static Dictionary<string, LanguageDefinition> Build() {
		Dictionary<string, LanguageDefinition> map = [with(StringComparer.OrdinalIgnoreCase)];

		foreach (LanguageDefinition language in (LanguageDefinition[])[CSharp, JavaScript, Json, Xml, Shell, Ini]) {
			foreach (string extension in language.Extensions) map[extension] = language;
		}

		return map;
	}
}
