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
		EnsureScratch(span.Length);
		snapshot.CopyTo(span.Start, span.Length, _scratch);

		TextMate.GrammarState start = line < _grammarValidUpTo && line < _grammarStates.Length
			? _grammarStates[line] ?? TextMate.GrammarState.Root
			: TextMate.GrammarState.Root;

		_scoped.Clear();

		tokenizer.Tokenize(new string(_scratch, 0, span.Length), start, _scoped);

		entry.Tokens.Clear();

		foreach (TextMate.ScopedToken token in _scoped) {
			entry.Tokens.Add(new ColoredToken(token.Start, token.Length,
				TextMate.Grammars.ColorFor(token.Scopes)));
		}

		entry.Line = line;
		return entry.Tokens;
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
			TextSpan span = snapshot.GetLineSpan(line);
			EnsureScratch(span.Length);
			snapshot.CopyTo(span.Start, span.Length, _scratch);

			discard.Clear();

			TextMate.GrammarState end = tokenizer.Tokenize(new string(_scratch, 0, span.Length),
				_grammarStates[line] ?? TextMate.GrammarState.Root, discard);

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
