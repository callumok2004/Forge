using System.Text.RegularExpressions;

namespace Forge.Extensions.Dotnet.Language;

public static partial class Preprocessor
{
	private readonly record struct Token(bool Identifier, string Value);

	private struct Frame(bool parentActive, bool taken, bool active)
	{
		public readonly bool ParentActive = parentActive;
		public bool Taken = taken;
		public bool Active = active;
	}

	[GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_]*)|(\|\||&&|==|!=|!|\(|\))")]
	private static partial Regex TokenPattern();

	[GeneratedRegex(@"^\s*#\s*(if|elif|else|endif)\b(.*)$")]
	private static partial Regex DirectivePattern();

	[GeneratedRegex(@"\r?\n")]
	private static partial Regex LineBreak();

	private static List<Token> Tokenize(string condition) {
		List<Token> tokens = [];

		foreach (Match match in TokenPattern().Matches(condition)) tokens.Add(match.Groups[1].Success ? new Token(true, match.Groups[1].Value) : new Token(false, match.Groups[2].Value));

		return tokens;
	}

	private static bool Evaluate(List<Token> tokens, IReadOnlySet<string> defines) {
		int index = 0;

		Token? Peek() => index < tokens.Count ? tokens[index] : null;

		bool Eat(string value) {
			if (Peek() is { Identifier: false } token && token.Value == value) {
				index++;
				return true;
			}

			return false;
		}

		bool Primary() {
			if (Eat("(")) {
				bool value = Or();
				Eat(")");
				return value;
			}

			if (Peek() is { Identifier: true } token) {
				index++;

				return token.Value switch {
					"true" => true,
					"false" => false,
					_ => defines.Contains(token.Value),
				};
			}

			index++;
			return false;
		}

		bool Unary() => Eat("!") ? !Unary() : Primary();

		bool Equality() {
			bool value = Unary();

			while (true) {
				if (Eat("==")) value = value == Unary();
				else if (Eat("!=")) value = value != Unary();
				else return value;
			}
		}

		bool And() {
			bool value = Equality();

			while (Eat("&&")) value = Equality() && value;

			return value;
		}

		bool Or() {
			bool value = And();

			while (Eat("||")) value = And() || value;

			return value;
		}

		return Or();
	}

	private static List<Frame> FramesAt(string text, int line, IReadOnlySet<string> defines) {
		string[] lines = LineBreak().Split(text);
		List<Frame> stack = [];

		bool Active() => stack.Count == 0 || stack[^1].Active;

		for (int i = 0; i < Math.Min(line + 1, lines.Length); i++) {
			Match directive = DirectivePattern().Match(lines[i]);

			if (!directive.Success) continue;

			string keyword = directive.Groups[1].Value;
			string rest = directive.Groups[2].Value;

			if (keyword == "endif") {
				if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);

				continue;
			}

			if (keyword == "if") {
				bool parentActive = Active();
				bool result = parentActive && Evaluate(Tokenize(rest), defines);
				stack.Add(new Frame(parentActive, result, result));
				continue;
			}

			if (stack.Count == 0) continue;

			Frame frame = stack[^1];
			stack.RemoveAt(stack.Count - 1);

			bool value = keyword == "elif"
				? frame.ParentActive && !frame.Taken && Evaluate(Tokenize(rest), defines)
				: frame.ParentActive && !frame.Taken;

			stack.Add(new Frame(frame.ParentActive, frame.Taken || value, value));
		}

		return stack;
	}

	public static bool IsActiveAt(string text, int line, IReadOnlySet<string> defines) {
		List<Frame> stack = FramesAt(text, line, defines);
		return stack.Count == 0 || stack[^1].Active;
	}

	public static bool IsConditionalAt(string text, int line) => FramesAt(text, line, new HashSet<string>()).Count > 0;

	public static HashSet<string> ParseDefineConstants(string? value) {
		HashSet<string> defines = new(StringComparer.Ordinal);

		foreach (string part in (value ?? string.Empty).Split([';', ','])) {
			string trimmed = part.Trim();

			if (trimmed.Length > 0) defines.Add(trimmed);
		}

		return defines;
	}
}
