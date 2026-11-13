using Forge.Text;

namespace Forge.Syntax;

public sealed class Highlighter(LanguageDefinition language)
{
	private const int CatchUpBudget = 20_000;

	private readonly List<Token> _tokens = [];
	private readonly List<Token> _discard = [];

	private LineState[] _startStates = new LineState[1024];
	private char[] _scratch = new char[512];

	private int _validUpTo;

	public LanguageDefinition Language { get; private set; } = language;

	public bool IsEnabled => Language != Languages.PlainText;

	public void SetLanguage(LanguageDefinition language) {
		Language = language;
		_validUpTo = 0;
		_grammarValidUpTo = 0;
		_grammarChecked = false;
		_grammar = null;

		InvalidateFrom(0);
	}

	public void InvalidateFrom(int line) {
		line = Math.Max(0, line);

		_validUpTo = Math.Min(_validUpTo, line);
		_grammarValidUpTo = Math.Min(_grammarValidUpTo, line);

		for (int i = 0; i < _colorCache.Length; i++) {
			if (_colorCache[i] is { } entry && entry.Line >= line) entry.Line = -1;
		}
	}

	#region TextMate

	private TextMate.GrammarTokenizer? _grammar;
	private bool _grammarChecked;
	private TextMate.GrammarState[] _grammarStates = new TextMate.GrammarState[1024];
	private int _grammarValidUpTo;

	private readonly List<TextMate.ScopedToken> _scoped = [];

	public readonly record struct ColoredToken(int Start, int Length, Theming.Color Color);

	private const int ColorCacheLines = 256;

	private readonly CachedLine?[] _colorCache = new CachedLine?[ColorCacheLines];

	private sealed class CachedLine
	{
		public int Line = -1;

		public readonly List<ColoredToken> Tokens = [];
	}

	private TextMate.GrammarTokenizer? GrammarFor() {
		if (_grammarChecked) return _grammar;

		_grammarChecked = true;

		_grammar = TextMate.Grammars.For(Language) is { } grammar
			? new TextMate.GrammarTokenizer(grammar)
			: null;

		return _grammar;
	}

	public bool HasGrammar => GrammarFor() is not null;

	public List<ColoredToken>? ColorLine(TextSnapshot snapshot, int line) {
		if (GrammarFor() is not { } tokenizer) return null;

		CachedLine entry = _colorCache[(uint)line % ColorCacheLines] ??= new CachedLine();

		if (entry.Line == line) return entry.Tokens;

		EnsureGrammarStates(snapshot, tokenizer, line);

		TextSpan span = snapshot.GetLineSpan(line);

		TextMate.GrammarState start = line < _grammarValidUpTo && line < _grammarStates.Length
			? _grammarStates[line] ?? TextMate.GrammarState.Root
			: TextMate.GrammarState.Root;

		_scoped.Clear();

		tokenizer.Tokenize(GrammarLine(snapshot, span), start, _scoped);

		entry.Tokens.Clear();

		foreach (TextMate.ScopedToken token in _scoped) {
			int length = Math.Min(token.Length, span.Length - token.Start);

			if (length <= 0) continue;

			entry.Tokens.Add(new ColoredToken(token.Start, length, TextMate.Grammars.ColorFor(token.Scopes)));
		}

		if (Language.GrammarId == "csharp") ColorSplitDeclaration(snapshot, line, span, entry.Tokens);

		entry.Line = line;
		return entry.Tokens;
	}

	private static readonly System.Text.RegularExpressions.Regex SplitName = new(@"^\s*(@?[_\p{L}][_\p{L}\p{Nd}]*)\s*(<[^>]*>)?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
	private static readonly System.Text.RegularExpressions.Regex DeclarationEnd = new(@"\b(class|struct|interface|record|enum)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
	private static readonly System.Text.RegularExpressions.Regex BaseList = new(@"^\s*:", System.Text.RegularExpressions.RegexOptions.Compiled);
	private static readonly System.Text.RegularExpressions.Regex TypeName = new(@"@?[_\p{L}][_\p{L}\p{Nd}]*", System.Text.RegularExpressions.RegexOptions.Compiled);

	private static readonly string[] ClassNameScopes = ["source.cs", "entity.name.type.class.cs"];
	private static readonly string[] BaseTypeScopes = ["source.cs", "entity.other.inherited-class.cs"];

	private static void ColorSplitDeclaration(TextSnapshot snapshot, int line, TextSpan span, List<ColoredToken> tokens) {
		string text = snapshot.GetText(span);
		bool name = SplitName.IsMatch(text);
		bool bases = !name && BaseList.IsMatch(text);

		if (!name && !bases) return;

		int previous = line - 1;

		for (; previous >= 0 && line - previous <= 32; previous--) {
			string candidate = snapshot.GetText(snapshot.GetLineSpan(previous)).Trim();

			if (candidate.Length == 0 || candidate[0] == '#') continue;

			if (bases && SplitName.IsMatch(candidate)) continue;

			break;
		}

		if (previous < 0 || !DeclarationEnd.IsMatch(snapshot.GetText(snapshot.GetLineSpan(previous)))) return;

		if (name) {
			System.Text.RegularExpressions.Match match = SplitName.Match(text);
			Recolor(tokens, match.Groups[1].Index, match.Groups[1].Length, TextMate.Grammars.ColorFor(ClassNameScopes));
			return;
		}

		Theming.Color color = TextMate.Grammars.ColorFor(BaseTypeScopes);

		foreach (System.Text.RegularExpressions.Match match in TypeName.Matches(text, text.IndexOf(':') + 1)) {
			if (match.Value is "where" or "new" or "class" or "struct" or "unmanaged" or "notnull") break;

			Recolor(tokens, match.Index, match.Length, color);
		}
	}

	private static void Recolor(List<ColoredToken> tokens, int start, int length, Theming.Color color) {
		int end = start + length;
		List<ColoredToken> result = [with(tokens.Count + 2)];

		foreach (ColoredToken token in tokens) {
			int tokenEnd = token.Start + token.Length;

			if (tokenEnd <= start || token.Start >= end) {
				result.Add(token);
				continue;
			}

			if (token.Start < start) result.Add(new ColoredToken(token.Start, start - token.Start, token.Color));

			if (tokenEnd > end) result.Add(new ColoredToken(end, tokenEnd - end, token.Color));
		}

		result.Add(new ColoredToken(start, length, color));
		result.Sort(static (a, b) => a.Start.CompareTo(b.Start));
		tokens.Clear();
		tokens.AddRange(result);
	}

	private static readonly System.Text.RegularExpressions.Regex StandardTokenScope = new(@"\b(comment|string|regex|meta\.embedded)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

	public bool IsCommentAt(TextSnapshot snapshot, int line, int column) {
		if (GrammarFor() is not { } tokenizer) {
			foreach (Token token in TokenizeLine(snapshot, line)) {
				if (column >= token.Start && column < token.End) return token.Kind is TokenKind.Comment or TokenKind.DocComment;
			}

			return false;
		}

		EnsureGrammarStates(snapshot, tokenizer, line);

		TextSpan span = snapshot.GetLineSpan(line);
		TextMate.GrammarState start = line < _grammarValidUpTo && line < _grammarStates.Length ? _grammarStates[line] ?? TextMate.GrammarState.Root : TextMate.GrammarState.Root;
		List<TextMate.ScopedToken> scoped = [];

		tokenizer.Tokenize(GrammarLine(snapshot, span), start, scoped);

		foreach (TextMate.ScopedToken token in scoped) {
			if (column < token.Start || column >= token.Start + token.Length) continue;

			string? kind = null;

			foreach (string scope in token.Scopes) {
				System.Text.RegularExpressions.Match match = StandardTokenScope.Match(scope);

				if (match.Success) kind = match.Groups[1].Value;
			}

			return kind == "comment";
		}

		return false;
	}

	private string GrammarLine(TextSnapshot snapshot, TextSpan span) {
		EnsureScratch(span.Length + 1);
		snapshot.CopyTo(span.Start, span.Length, _scratch);
		_scratch[span.Length] = '\n';
		return new string(_scratch, 0, span.Length + 1);
	}

	private void EnsureGrammarStates(TextSnapshot snapshot, TextMate.GrammarTokenizer tokenizer,
		int targetLine) {
		if (targetLine < _grammarValidUpTo) return;

		if (snapshot.LineCount + 1 > _grammarStates.Length) {
			int capacity = _grammarStates.Length;

			while (capacity < snapshot.LineCount + 1) capacity *= 2;

			Array.Resize(ref _grammarStates, capacity);
		}

		int limit = Math.Min(targetLine + 1, _grammarValidUpTo + CatchUpBudget);

		List<TextMate.ScopedToken> discard = _scoped;

		for (int line = _grammarValidUpTo; line < limit && line < snapshot.LineCount; line++) {
			discard.Clear();

			TextMate.GrammarState end = tokenizer.Tokenize(GrammarLine(snapshot, snapshot.GetLineSpan(line)), _grammarStates[line] ?? TextMate.GrammarState.Root, discard);

			if (line + 1 < _grammarStates.Length) _grammarStates[line + 1] = end;
		}

		_grammarValidUpTo = Math.Max(_grammarValidUpTo, limit);
	}

	#endregion

	public List<Token> TokenizeLine(TextSnapshot snapshot, int line) {
		_tokens.Clear();

		if (!IsEnabled) return _tokens;

		EnsureStates(snapshot, line);

		TextSpan span = snapshot.GetLineSpan(line);
		EnsureScratch(span.Length);
		snapshot.CopyTo(span.Start, span.Length, _scratch);

		LineState start = line < _validUpTo ? _startStates[line] : LineState.Normal;
		CodeTokenizer.Tokenize(_scratch.AsSpan(0, span.Length), start, Language, _tokens);
		return _tokens;
	}

	private void EnsureStates(TextSnapshot snapshot, int targetLine) {
		if (targetLine < _validUpTo) return;

		EnsureStateCapacity(snapshot.LineCount);

		int limit = Math.Min(targetLine + 1, _validUpTo + CatchUpBudget);

		List<Token> discard = _discard;

		for (int line = _validUpTo; line < limit && line < snapshot.LineCount; line++) {
			TextSpan span = snapshot.GetLineSpan(line);
			EnsureScratch(span.Length);
			snapshot.CopyTo(span.Start, span.Length, _scratch);

			LineState start = _startStates[line];
			discard.Clear();
			LineState end = CodeTokenizer.Tokenize(_scratch.AsSpan(0, span.Length), start, Language, discard);

			if (line + 1 < _startStates.Length) _startStates[line + 1] = end;
		}

		_validUpTo = Math.Max(_validUpTo, limit);
	}

	private void EnsureStateCapacity(int lineCount) {
		if (lineCount + 1 <= _startStates.Length) return;

		int capacity = _startStates.Length;
		while (capacity < lineCount + 1) capacity *= 2;

		Array.Resize(ref _startStates, capacity);
	}

	private void EnsureScratch(int required) {
		if (required <= _scratch.Length) return;

		int capacity = _scratch.Length;
		while (capacity < required) capacity *= 2;

		_scratch = new char[capacity];
	}
}
