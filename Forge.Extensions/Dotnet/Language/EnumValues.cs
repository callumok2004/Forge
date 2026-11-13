using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

using Forge.Common.Editor;

namespace Forge.Extensions.Dotnet.Language;

public sealed partial class EnumValues
{
	public readonly record struct Member(string Enum, string Name, int Line, BigInteger? Value, bool Literal);

	private readonly Lock _sync = new();
	private string? _path;
	private int _version = -1;
	private IReadOnlyDictionary<int, LineHint>? _hints;

	[GeneratedRegex(@"\benum\s+(@?[_\p{L}][_\p{L}\p{Nd}]*)\s*(?::\s*[\w.]+\s*)?\{")]
	private static partial Regex EnumHeader();

	[GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(@?[_\p{L}][_\p{L}\p{Nd}]*)\s*(?:=\s*(.+?))?\s*$", RegexOptions.Singleline)]
	private static partial Regex MemberPattern();

	[GeneratedRegex(@"^\s*(?:0[xX][0-9a-fA-F_]+|0[bB][01_]+|[0-9][0-9_]*)[uUlL]*\s*$")]
	private static partial Regex NumericLiteral();

	public IReadOnlyDictionary<int, LineHint>? Hints(string path, int version, Func<string?> text) {
		lock (_sync) {
			if (_version == version && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return _hints;
		}

		Dictionary<int, LineHint> hints = [];

		if (text() is { } source) {
			foreach (Member member in Parse(source)) {
				if (member.Value is { } value && !member.Literal) hints.TryAdd(member.Line, new LineHint($"= {Format(value)}", false));
			}
		}

		IReadOnlyDictionary<int, LineHint>? result = hints.Count > 0 ? hints : null;

		lock (_sync) {
			_path = path;
			_version = version;
			_hints = result;
		}

		return result;
	}

	public static string Format(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);

	[GeneratedRegex(@"(@?[_\p{L}][_\p{L}\p{Nd}]*)\.(@?[_\p{L}][_\p{L}\p{Nd}]*) = ([^\n`]+)")]
	private static partial Regex HoverMember();

	public static string? AppendToHover(string hover, IEnumerable<string> texts) {
		Match match = HoverMember().Match(hover);

		if (!match.Success || NumericLiteral().IsMatch(match.Groups[3].Value)) return null;

		string enumName = match.Groups[1].Value.TrimStart('@');
		string memberName = match.Groups[2].Value.TrimStart('@');

		foreach (string text in texts) {
			if (!text.Contains(memberName, StringComparison.Ordinal)) continue;

			foreach (Member member in Parse(text)) {
				if (member.Enum == enumName && member.Name == memberName && member.Value is { } value) return string.Concat(hover, "\n\nValue: `", Format(value), "`");
			}
		}

		return null;
	}

	public static List<Member> Parse(string text) {
		string code = Mask(text);
		List<Member> members = [];
		List<int> lineStarts = [0];

		for (int i = 0; i < text.Length; i++) {
			if (text[i] == '\n') lineStarts.Add(i + 1);
		}

		foreach (Match header in EnumHeader().Matches(code)) {
			string name = header.Groups[1].Value.TrimStart('@');
			int bodyStart = header.Index + header.Length;
			int bodyEnd = code.IndexOf('}', bodyStart);

			if (bodyEnd < 0) continue;

			Dictionary<string, BigInteger> known = new(StringComparer.Ordinal);
			BigInteger? next = 0;
			int segmentStart = bodyStart;

			for (int i = bodyStart; i <= bodyEnd; i++) {
				if (i < bodyEnd && code[i] != ',') continue;

				string segment = code[segmentStart..i];
				string withoutDirectives = string.Join('\n', segment.Split('\n').Where(static line => !line.TrimStart().StartsWith('#')));
				Match match = MemberPattern().Match(withoutDirectives);

				if (match.Success && withoutDirectives.Trim().Length > 0) {
					string memberName = match.Groups[1].Value.TrimStart('@');
					int nameOffset = segmentStart + segment.IndexOf(match.Groups[1].Value, StringComparison.Ordinal);
					BigInteger? value;
					bool literal = false;

					if (match.Groups[2].Success) {
						string expression = match.Groups[2].Value;
						literal = NumericLiteral().IsMatch(expression);
						value = new Evaluator(expression, known).Evaluate();
					}
					else value = next;

					if (value is { } resolved) known[memberName] = resolved;

					next = value + 1;
					members.Add(new Member(name, memberName, LineOf(lineStarts, nameOffset), value, literal));
				}

				segmentStart = i + 1;
			}
		}

		return members;
	}

	private static int LineOf(List<int> lineStarts, int offset) {
		int index = lineStarts.BinarySearch(offset);
		return index >= 0 ? index : ~index - 1;
	}

	private static string Mask(string text) {
		char[] output = text.ToCharArray();
		int i = 0;

		while (i < output.Length) {
			if (i + 1 < output.Length && output[i] == '/' && output[i + 1] == '/') {
				while (i < output.Length && output[i] != '\n') output[i++] = ' ';
			}
			else if (i + 1 < output.Length && output[i] == '/' && output[i + 1] == '*') {
				while (i < output.Length && !(output[i] == '*' && i + 1 < output.Length && output[i + 1] == '/')) {
					if (output[i] != '\n') output[i] = ' ';

					i++;
				}

				if (i < output.Length) output[i++] = ' ';

				if (i < output.Length) output[i++] = ' ';
			}
			else if (output[i] == '"') {
				i++;

				while (i < output.Length && output[i] != '"' && output[i] != '\n') {
					if (output[i] == '\\') output[i++] = ' ';

					if (i < output.Length) output[i++] = ' ';
				}

				i++;
			}
			else i++;
		}

		return new string(output);
	}

	private sealed class Evaluator(string text, Dictionary<string, BigInteger> known)
	{
		private int _position;

		public BigInteger? Evaluate() {
			try {
				BigInteger? value = Or();
				Skip();
				return _position == text.Length ? value : null;
			}
			catch (Exception exception) when (exception is FormatException or DivideByZeroException or OverflowException or ArgumentException) {
				return null;
			}
		}

		private void Skip() {
			while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
		}

		private bool Eat(string token) {
			Skip();

			if (string.CompareOrdinal(text, _position, token, 0, token.Length) != 0) return false;

			if (token is "|" or "&" or "<" or ">" && _position + 1 < text.Length && text[_position + 1] == token[0]) return false;

			_position += token.Length;
			return true;
		}

		private BigInteger? Or() {
			BigInteger? value = Xor();

			while (value is not null && Eat("|")) value |= Xor();

			return value;
		}

		private BigInteger? Xor() {
			BigInteger? value = And();

			while (value is not null && Eat("^")) value ^= And();

			return value;
		}

		private BigInteger? And() {
			BigInteger? value = Shift();

			while (value is not null && Eat("&")) value &= Shift();

			return value;
		}

		private BigInteger? Shift() {
			BigInteger? value = Additive();

			while (value is not null) {
				if (Eat("<<")) value <<= (int)(Additive() ?? throw new FormatException());
				else if (Eat(">>")) value >>= (int)(Additive() ?? throw new FormatException());
				else break;
			}

			return value;
		}

		private BigInteger? Additive() {
			BigInteger? value = Multiplicative();

			while (value is not null) {
				if (Eat("+")) value += Multiplicative();
				else if (Eat("-")) value -= Multiplicative();
				else break;
			}

			return value;
		}

		private BigInteger? Multiplicative() {
			BigInteger? value = Unary();

			while (value is not null) {
				if (Eat("*")) value *= Unary();
				else if (Eat("/")) value /= Unary();
				else if (Eat("%")) value %= Unary();
				else break;
			}

			return value;
		}

		private BigInteger? Unary() {
			if (Eat("~")) return -(Unary() + 1);

			if (Eat("-")) return -Unary();

			if (Eat("+")) return Unary();

			return Primary();
		}

		private BigInteger? Primary() {
			Skip();

			if (Eat("(")) {
				int saved = _position;
				Skip();

				if (Identifier() is { } cast && Eat(")") && IsIntegralType(cast)) return Unary();

				_position = saved;
				BigInteger? inner = Or();
				return Eat(")") ? inner : null;
			}

			if (_position < text.Length && char.IsDigit(text[_position])) return Number();

			if (_position < text.Length && text[_position] == '\'') return Character();

			if (Identifier() is not { } name) return null;

			while (Eat(".")) {
				if (Identifier() is not { } part) return null;

				name = part;
			}

			return known.TryGetValue(name, out BigInteger value) ? value : null;
		}

		private static bool IsIntegralType(string name) => name is "int" or "uint" or "long" or "ulong" or "short" or "ushort" or "byte" or "sbyte" or "char" or "nint" or "nuint";

		private string? Identifier() {
			Skip();
			int start = _position;

			if (_position < text.Length && text[_position] == '@') _position++;

			while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;

			string name = text[start.._position].TrimStart('@');

			if (name.Length == 0 || char.IsDigit(name[0])) {
				_position = start;
				return null;
			}

			return name;
		}

		private BigInteger Number() {
			int start = _position;

			while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;

			string literal = text[start.._position].Replace("_", string.Empty).TrimEnd('u', 'U', 'l', 'L');

			if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return BigInteger.Parse("0" + literal[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

			if (literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) {
				BigInteger value = 0;

				foreach (char c in literal[2..]) value = (value << 1) + (c == '1' ? 1 : c == '0' ? 0 : throw new FormatException());

				return value;
			}

			return BigInteger.Parse(literal, NumberStyles.None, CultureInfo.InvariantCulture);
		}

		private BigInteger Character() {
			_position++;
			char value = text[_position] == '\\' ? text[++_position] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => text[_position] } : text[_position];
			_position++;

			if (_position >= text.Length || text[_position] != '\'') throw new FormatException();

			_position++;
			return value;
		}
	}
}
