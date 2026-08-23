namespace Forge.VSCode;

public static class WhenExpression
{
	public static bool Evaluate(string? expression, IReadOnlyDictionary<string, string> context) {
		if (expression is not { Length: > 0 }) return true;

		return EvaluateOr(expression.AsSpan(), context);
	}

	private static bool EvaluateOr(ReadOnlySpan<char> expression, IReadOnlyDictionary<string, string> context) {
		int split = Split(expression, "||");

		if (split < 0) return EvaluateAnd(expression, context);

		return EvaluateAnd(expression[..split], context) ||
			EvaluateOr(expression[(split + 2)..], context);
	}

	private static bool EvaluateAnd(ReadOnlySpan<char> expression, IReadOnlyDictionary<string, string> context) {
		int split = Split(expression, "&&");

		if (split < 0) return EvaluateTerm(expression, context);

		return EvaluateTerm(expression[..split], context) &&
			EvaluateAnd(expression[(split + 2)..], context);
	}

	private static bool EvaluateTerm(ReadOnlySpan<char> expression, IReadOnlyDictionary<string, string> context) {
		ReadOnlySpan<char> term = expression.Trim();

		if (term.Length == 0) return true;

		if (term[0] == '(' && term[^1] == ')') return EvaluateOr(term[1..^1], context);

		if (term[0] == '!') return !EvaluateTerm(term[1..], context);

		int equality = term.IndexOf("==".AsSpan());

		if (equality >= 0) {
			return string.Equals(Value(term[..equality], context), Literal(term[(equality + 2)..]),
				StringComparison.Ordinal);
		}

		int inequality = term.IndexOf("!=".AsSpan());

		if (inequality >= 0) {
			return !string.Equals(Value(term[..inequality], context), Literal(term[(inequality + 2)..]),
				StringComparison.Ordinal);
		}

		string value = Value(term, context);

		return value.Length > 0 && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
	}

	private static string Value(ReadOnlySpan<char> key, IReadOnlyDictionary<string, string> context) =>
		context.TryGetValue(key.Trim().ToString(), out string? value) ? value : string.Empty;

	private static string Literal(ReadOnlySpan<char> value) {
		ReadOnlySpan<char> trimmed = value.Trim();

		return trimmed.Length >= 2 && (trimmed[0] == '\'' || trimmed[0] == '"')
			? trimmed[1..^1].ToString()
			: trimmed.ToString();
	}

	private static int Split(ReadOnlySpan<char> expression, string separator) {
		int depth = 0;

		for (int i = 0; i < expression.Length - 1; i++) {
			if (expression[i] == '(') depth++;
			else if (expression[i] == ')') depth--;
			else if (depth == 0 && expression[i] == separator[0] && expression[i + 1] == separator[1]) return i;
		}

		return -1;
	}
}
