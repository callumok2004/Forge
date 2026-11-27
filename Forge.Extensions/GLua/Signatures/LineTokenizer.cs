namespace Forge.Extensions.GLua.Signatures;

public sealed class LineTokenizer
{
	public const int MaxLineLength = 20000;

	public static readonly Dictionary<char, string> EscapeSequences = new() {
		['a'] = "␇", ['b'] = "[BS]", ['f'] = "\f", ['n'] = "\n", ['r'] = "\r", ['t'] = "\t", ['v'] = "\v", ['\\'] = "\\", ['"'] = "\"", ['\''] = "'",
	};

	private string _token = string.Empty;
	private char _openString;
	private int _openMultiline = -1;
	private bool _openEscape;
	private bool _openShortCall;

	public bool InvalidLua { get; private set; }

	public List<List<string>?> OpenParentheses { get; } = [];

	public LineTokenizer(string text) {
		if (text.Length == 0 || text.Length > MaxLineLength) return;

		Tokenize(text);
	}

	private static bool IsName(string part) =>
		part.Length > 0 && (char.IsAsciiLetter(part[0]) || part[0] == '_') && part.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

	private void Open() {
		OpenParentheses.Add([_token]);
		_token = string.Empty;
	}

	private void Close() {
		if (OpenParentheses.Count > 0) OpenParentheses.RemoveAt(OpenParentheses.Count - 1);

		_token = string.Empty;
	}

	private void Tokenize(string text) {
		int i = -1;

		while (i < text.Length - 1) {
			i++;
			char character = text[i];

			if (_openString != '\0') {
				if (_openEscape) {
					if (!EscapeSequences.TryGetValue(character, out string? escaped)) {
						InvalidLua = true;
						return;
					}

					_openEscape = false;
					_token += escaped;
				}
				else if (character == '\\') _openEscape = true;
				else if (character == _openString) {
					_openString = '\0';
					_token += "\"";

					if (_openShortCall) {
						if (OpenParentheses.Count > 0) OpenParentheses.RemoveAt(OpenParentheses.Count - 1);

						_token = string.Empty;
					}

					_openShortCall = false;
				}
				else _token += character;

				continue;
			}

			if (_openMultiline >= 0) {
				if (character != ']') {
					_token += character;
					continue;
				}

				int equals = 0;
				string tail = string.Empty;
				string closeTail = string.Empty;
				bool closed = false;

				while (i < text.Length) {
					i++;
					char next = i < text.Length ? text[i] : '\0';

					if (next == '=') equals++;
					else if (next == ']') {
						if (equals == _openMultiline) {
							_openMultiline = -1;
							_token += closeTail;
							closed = true;
							break;
						}
					}
					else closeTail += next;

					tail += next;
				}

				if (!closed) _token += tail;

				continue;
			}

			if (character is ' ' or '\t') continue;

			if (character == '[') {
				int equals = 0;

				while (i < text.Length) {
					i++;
					char next = i < text.Length ? text[i] : '\0';

					if (next == '=') {
						equals++;
						continue;
					}

					if (next == '[') {
						_openMultiline = equals;
						_token = string.Empty;
						break;
					}

					InvalidLua = true;
					return;
				}

				continue;
			}

			if (_token == "local") {
				_token = string.Empty;
				continue;
			}

			switch (character) {
				case '\n':
					InvalidLua = true;
					return;
				case '\'':
				case '"':
					if (_token.Length > 0) {
						_openShortCall = true;
						Open();
					}

					_openString = character;
					_token += "\"";
					break;
				case '(':
					if (_token.Length > 0) {
						if (_token.Split(':', '.').All(IsName)) Open();
						else {
							InvalidLua = true;
							break;
						}
					}
					else OpenParentheses.Add(null);

					break;
				case ')':
					Close();
					break;
				case ',':
					if (_openShortCall) {
						InvalidLua = true;
						return;
					}

					if (OpenParentheses.Count > 0) {
						if (OpenParentheses[^1] is not { } call) {
							InvalidLua = true;
							return;
						}

						call.Add(_token);
						_token = string.Empty;
						break;
					}

					_token = string.Empty;
					continue;
				case '=':
					_token = string.Empty;
					continue;
				case '.' when i + 1 < text.Length && text[i + 1] == '.':
					i++;
					break;
				default:
					_token += character;
					break;
			}
		}
	}
}
