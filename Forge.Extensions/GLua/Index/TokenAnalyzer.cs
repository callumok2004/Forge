using Forge.Extensions.GLua.Parsing;

namespace Forge.Extensions.GLua.Index;

public static class TokenAnalyzer
{
	public static List<string>? FullFunctionCall(LuaNode token, Func<LuaNode, bool>? callback = null) {
		List<string> call = [];

		if (!Discover(token, call, callback)) return null;

		call.Reverse();
		return call;
	}

	private static bool Discover(LuaNode token, List<string> call, Func<LuaNode, bool>? callback) {
		if (callback is not null && !callback(token)) return false;

		if (token.Type == LuaNodeType.CallStatement) return token.Expression?.Base is not { } expressionBase || Discover(expressionBase, call, callback);

		if (token.Identifier is { } identifier) {
			if (callback is not null && !callback(identifier)) return false;

			call.Add(identifier.Name ?? "undefined");

			if (token.Indexer != '\0') call.Add(token.Indexer.ToString());
		}
		else if (token.Name is { Length: > 0 } name) call.Add(name);

		return !token.HasBase || token.Base is not { } callee || Discover(callee, call, callback);
	}

	public static List<LuaNode>? FunctionArguments(LuaNode? token) {
		while (token is not null) {
			if (token.Type is LuaNodeType.CallExpression or LuaNodeType.ReturnStatement) return token.Arguments;

			if (token.Type == LuaNodeType.TableCallExpression) return null;

			if (token.Type == LuaNodeType.CallStatement && token.Expression?.Arguments is { } arguments) return arguments;

			if (token.Parent is { Type: LuaNodeType.CallStatement or LuaNodeType.CallExpression or LuaNodeType.IndexExpression or LuaNodeType.MemberExpression } parent) token = parent;
			else break;
		}

		return null;
	}

	public static string? StringLiteral(LuaNode token) {
		if (token.Type == LuaNodeType.StringLiteral) return token.Text is { Length: > 0 } text ? text : null;

		if (token.Type == LuaNodeType.BinaryExpression && token.Operator == ".." && StringLiteral(token.Left!) is { } left && StringLiteral(token.Right!) is { } right) return left + right;

		return null;
	}

	public static (List<string> Call, LuaNode Target) QualifyMemberExpression(LuaNode token, int line = -1, int column = -1) {
		List<string> call = [];
		LuaNode root = token;

		while (root.HasBase && root.Base is { } next) root = next;

		LuaNode parent = root;

		while (true) {
			if (parent.Type is not (LuaNodeType.MemberExpression or LuaNodeType.Identifier or LuaNodeType.CallExpression)) break;

			LuaNode identifier = parent.Identifier ?? parent;

			if (parent.Indexer != '\0') call.Add(parent.Indexer.ToString());

			if (parent.Type == LuaNodeType.CallExpression) {
				if (call.Count > 0) call[^1] += "()";
			}
			else call.Add(identifier.Name ?? "undefined");

			if (line >= 0 && identifier.Contains(line, column)) break;

			if (parent.Parent is { } up) parent = up;
			else break;
		}

		return (call, parent);
	}
}
