using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua.Parsing;

public enum LuaNodeType : byte
{
	LabelStatement,
	BreakStatement,
	ContinueStatement,
	GotoStatement,
	ReturnStatement,
	IfStatement,
	IfClause,
	ElseifClause,
	ElseClause,
	WhileStatement,
	DoStatement,
	RepeatStatement,
	LocalStatement,
	AssignmentStatement,
	CallStatement,
	FunctionDeclaration,
	ForNumericStatement,
	ForGenericStatement,
	Chunk,
	Identifier,
	StringLiteral,
	NumericLiteral,
	BooleanLiteral,
	NilLiteral,
	VarargLiteral,
	TableKey,
	TableKeyString,
	TableValue,
	TableConstructorExpression,
	BinaryExpression,
	LogicalExpression,
	UnaryExpression,
	MemberExpression,
	IndexExpression,
	CallExpression,
	TableCallExpression,
	StringCallExpression
}

public sealed class LuaNode(LuaNodeType type)
{
	public LuaNodeType Type { get; } = type;

	public string? Name { get; init; }

	public string? Text { get; init; }

	public string? Raw { get; init; }

	public string? Operator { get; init; }

	public char Indexer { get; init; }

	public bool IsLocal { get; set; }

	public bool InParens { get; set; }

	public LuaNode? Identifier { get; init; }

	public LuaNode? Base { get; init; }

	public LuaNode? Index { get; init; }

	public LuaNode? Argument { get; init; }

	public LuaNode? Key { get; init; }

	public LuaNode? Value { get; init; }

	public LuaNode? Expression { get; init; }

	public LuaNode? Label { get; init; }

	public LuaNode? Condition { get; init; }

	public LuaNode? Variable { get; init; }

	public LuaNode? Start { get; init; }

	public LuaNode? End { get; init; }

	public LuaNode? Step { get; init; }

	public LuaNode? Left { get; init; }

	public LuaNode? Right { get; init; }

	public List<LuaNode>? Arguments { get; init; }

	public List<LuaNode>? Body { get; init; }

	public List<LuaNode>? Parameters { get; init; }

	public List<LuaNode>? Variables { get; init; }

	public List<LuaNode>? Init { get; init; }

	public List<LuaNode>? Iterators { get; init; }

	public List<LuaNode>? Clauses { get; init; }

	public List<LuaNode>? Fields { get; init; }

	public int StartLine { get; set; }

	public int StartColumn { get; set; }

	public int EndLine { get; set; }

	public int EndColumn { get; set; }

	public LuaNode? Parent { get; set; }

	public Dictionary<string, LuaNode>? Scope { get; set; }

	public List<WikiDoc>? Docs { get; set; }

	public string? Path { get; set; }

	public bool HasBase => Type is LuaNodeType.MemberExpression or LuaNodeType.IndexExpression or LuaNodeType.CallExpression or LuaNodeType.TableCallExpression or LuaNodeType.StringCallExpression;

	public bool IsScopeController => Type is LuaNodeType.Chunk or LuaNodeType.IfClause or LuaNodeType.ElseClause or LuaNodeType.ElseifClause or LuaNodeType.WhileStatement or LuaNodeType.FunctionDeclaration or LuaNodeType.RepeatStatement or LuaNodeType.ForGenericStatement or LuaNodeType.ForNumericStatement or LuaNodeType.DoStatement;

	public bool Contains(int line, int column) {
		if (line < StartLine - 1 || line > EndLine - 1) return false;

		if (line == StartLine - 1 && column < StartColumn) return false;

		return line != EndLine - 1 || column <= EndColumn;
	}

	public bool Contains(LuaNode other) =>
		Contains(other.StartLine - 1, other.StartColumn) && Contains(other.EndLine - 1, other.EndColumn);

	public void Children(List<LuaNode> into) {
		switch (Type) {
			case LuaNodeType.AssignmentStatement:
			case LuaNodeType.LocalStatement:
				Add(into, Variables);
				Add(into, Init);
				break;
			case LuaNodeType.BinaryExpression:
			case LuaNodeType.LogicalExpression:
				Add(into, Left);
				Add(into, Right);
				break;
			case LuaNodeType.CallExpression:
				Add(into, Base);
				Add(into, Arguments);
				break;
			case LuaNodeType.CallStatement:
				Add(into, Expression);
				break;
			case LuaNodeType.DoStatement:
			case LuaNodeType.ElseClause:
			case LuaNodeType.Chunk:
				Add(into, Body);
				break;
			case LuaNodeType.ElseifClause:
			case LuaNodeType.IfClause:
			case LuaNodeType.WhileStatement:
			case LuaNodeType.RepeatStatement:
				Add(into, Condition);
				Add(into, Body);
				break;
			case LuaNodeType.ForGenericStatement:
				Add(into, Variables);
				Add(into, Iterators);
				Add(into, Body);
				break;
			case LuaNodeType.ForNumericStatement:
				Add(into, Variable);
				Add(into, Start);
				Add(into, End);
				Add(into, Body);
				Add(into, Step);
				break;
			case LuaNodeType.FunctionDeclaration:
				Add(into, Parameters);
				Add(into, Body);
				break;
			case LuaNodeType.GotoStatement:
			case LuaNodeType.LabelStatement:
				Add(into, Label);
				break;
			case LuaNodeType.IfStatement:
				Add(into, Clauses);
				break;
			case LuaNodeType.IndexExpression:
				Add(into, Base);
				Add(into, Index);
				break;
			case LuaNodeType.MemberExpression:
				Add(into, Base);
				break;
			case LuaNodeType.ReturnStatement:
				Add(into, Arguments);
				break;
			case LuaNodeType.StringCallExpression:
				Add(into, Base);
				Add(into, Argument);
				break;
			case LuaNodeType.TableConstructorExpression:
				Add(into, Fields);
				break;
			case LuaNodeType.TableKey:
			case LuaNodeType.TableKeyString:
				Add(into, Key);
				Add(into, Value);
				break;
			case LuaNodeType.TableValue:
				Add(into, Value);
				break;
			case LuaNodeType.UnaryExpression:
				Add(into, Argument);
				break;
		}
	}

	public bool HasChildren => Type is not (LuaNodeType.Identifier or LuaNodeType.StringLiteral or LuaNodeType.NumericLiteral or LuaNodeType.BooleanLiteral or LuaNodeType.NilLiteral or LuaNodeType.VarargLiteral or LuaNodeType.BreakStatement or LuaNodeType.ContinueStatement or LuaNodeType.TableCallExpression);

	private static void Add(List<LuaNode> into, LuaNode? node) {
		if (node is not null) into.Add(node);
	}

	private static void Add(List<LuaNode> into, List<LuaNode>? nodes) {
		if (nodes is not null) into.AddRange(nodes);
	}
}
