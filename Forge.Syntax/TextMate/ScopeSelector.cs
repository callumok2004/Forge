namespace Forge.Syntax.TextMate;

public sealed class ScopeSelector
{
	private abstract class Node
	{
		public abstract bool Matches(string root, IReadOnlyList<string> scopes);
	}

	private sealed class PathNode(string[] path) : Node
	{
		public override bool Matches(string root, IReadOnlyList<string> scopes) {
			int next = 0;

			for (int i = -1; i < scopes.Count && next < path.Length; i++) {
				if (ScopeMatches(i < 0 ? root : scopes[i], path[next])) next++;
			}

			return next == path.Length;
		}
	}

	private sealed class OrNode(Node left, Node right) : Node
	{
		public override bool Matches(string root, IReadOnlyList<string> scopes) => left.Matches(root, scopes) || right.Matches(root, scopes);
	}

	private sealed class AndNode(Node left, Node right, bool negate) : Node
	{
		public override bool Matches(string root, IReadOnlyList<string> scopes) => left.Matches(root, scopes) && right.Matches(root, scopes) != negate;
	}

	private sealed class AnyNode : Node
	{
		public override bool Matches(string root, IReadOnlyList<string> scopes) => true;
	}

	private readonly (Node Node, int Priority)[] _alternatives;

	private ScopeSelector((Node, int)[] alternatives) => _alternatives = alternatives;

	public bool Matches(string root, IReadOnlyList<string> scopes, out int priority) {
		foreach ((Node node, int alternative) in _alternatives) {
			if (!node.Matches(root, scopes)) continue;

			priority = alternative;
			return true;
		}

		priority = 0;
		return false;
	}

	public static ScopeSelector Parse(string selector) {
		List<string> tokens = Tokenize(selector);
		List<(Node, int)> alternatives = [];
		int position = 0;

		while (position < tokens.Count) {
			int priority = 0;

			if (position < tokens.Count && tokens[position] is { Length: > 1 } head && head[1] == ':' && head[0] is 'L' or 'R' or 'B') {
				priority = head[0] switch { 'L' => -1, 'R' => 1, _ => 0 };
				tokens[position] = head[2..];

				if (tokens[position].Length == 0) position++;
			}

			Node? node = ParseAnd(tokens, ref position);

			while (position < tokens.Count && tokens[position] == "|") {
				position++;

				if (ParseAnd(tokens, ref position) is { } right) node = node is null ? right : new OrNode(node, right);
			}

			alternatives.Add((node ?? new AnyNode(), priority));

			while (position < tokens.Count && tokens[position] is not ",") position++;

			position++;
		}

		return new ScopeSelector([.. alternatives]);
	}

	private static Node? ParseOr(List<string> tokens, ref int position) {
		Node? result = ParseAnd(tokens, ref position);

		while (position < tokens.Count && tokens[position] is "," or "|") {
			position++;

			if (ParseAnd(tokens, ref position) is { } right) result = result is null ? right : new OrNode(result, right);
		}

		return result;
	}

	private static Node? ParseAnd(List<string> tokens, ref int position) {
		Node? result = null;

		while (position < tokens.Count) {
			string token = tokens[position];

			if (token is "," or "|" or ")") break;

			bool negate = token == "-";

			if (negate || token == "&") position++;

			if (ParseTerm(tokens, ref position) is not { } term) break;

			result = result is null ? negate ? new AndNode(new AnyNode(), term, true) : term : new AndNode(result, term, negate);
		}

		return result;
	}

	private static Node? ParseTerm(List<string> tokens, ref int position) {
		if (position >= tokens.Count) return null;

		if (tokens[position] == "(") {
			position++;
			Node? inner = ParseOr(tokens, ref position);

			if (position < tokens.Count && tokens[position] == ")") position++;

			return inner;
		}

		List<string> path = [];

		while (position < tokens.Count && IsScope(tokens[position])) path.Add(tokens[position++]);

		return path.Count > 0 ? new PathNode([.. path]) : null;
	}

	private static bool IsScope(string token) => token is not ("(" or ")" or "," or "|" or "-" or "&");

	private static List<string> Tokenize(string selector) {
		List<string> tokens = [];
		int i = 0;

		while (i < selector.Length) {
			char c = selector[i];

			if (char.IsWhiteSpace(c)) {
				i++;
				continue;
			}

			if (c is '(' or ')' or ',' or '|' or '&') {
				tokens.Add(c.ToString());
				i++;
				continue;
			}

			int start = i;

			while (i < selector.Length && !char.IsWhiteSpace(selector[i]) && selector[i] is not ('(' or ')' or ',' or '|' or '&')) i++;

			tokens.Add(selector[start..i]);
		}

		return tokens;
	}

	private static bool ScopeMatches(string scope, string selector) {
		if (selector.Contains('*')) return WildcardMatches(scope, selector);

		return scope.StartsWith(selector, StringComparison.Ordinal) && (scope.Length == selector.Length || scope[selector.Length] == '.');
	}

	private static bool WildcardMatches(ReadOnlySpan<char> scope, ReadOnlySpan<char> selector) {
		while (!selector.IsEmpty) {
			if (scope.IsEmpty) return false;

			int selectorDot = selector.IndexOf('.');
			int scopeDot = scope.IndexOf('.');
			ReadOnlySpan<char> part = selectorDot < 0 ? selector : selector[..selectorDot];
			ReadOnlySpan<char> segment = scopeDot < 0 ? scope : scope[..scopeDot];

			if (part is not "*" && !part.SequenceEqual(segment)) return false;

			selector = selectorDot < 0 ? [] : selector[(selectorDot + 1)..];
			scope = scopeDot < 0 ? [] : scope[(scopeDot + 1)..];
		}

		return true;
	}
}
