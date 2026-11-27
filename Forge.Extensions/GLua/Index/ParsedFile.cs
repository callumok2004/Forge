using System.Text.RegularExpressions;

using Forge.Extensions.GLua.Completion;
using Forge.Extensions.GLua.Parsing;
using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua.Index;

public sealed class LineIndex
{
	private readonly Dictionary<int, int> _first = [];

	public int Max { get; private set; }

	public int? Min { get; private set; }

	public void Note(int index, int line) {
		if (_first.TryGetValue(line, out int existing) && index >= existing) return;

		Max = Math.Max(line, Max);
		Min ??= line;
		_first[line] = index;
	}

	public int? StartFor(int line) {
		if (Min is not { } min) return null;

		int search = line;

		while (!(_first.TryGetValue(search, out int found) && found > 0)) {
			search--;

			if (search <= min) {
				search = min;
				break;
			}
		}

		return _first.TryGetValue(search, out int start) ? start : null;
	}
}

public sealed class TypedTokens
{
	public List<LuaNode> List { get; } = [];

	public LineIndex Lines { get; } = new();
}

public sealed class ParsedFile
{
	public required string Path { get; init; }

	public required LuaNode Chunk { get; init; }

	public List<LuaNode> List { get; } = [];

	public LineIndex Lines { get; } = new();

	public Dictionary<LuaNodeType, TypedTokens> Types { get; } = [];

	public required FileTokenData Data { get; init; }

	public TypedTokens? Of(LuaNodeType type) => Types.GetValueOrDefault(type);

	public LuaNode? FindChunkAt(int line, int column) {
		if (Lines.StartFor(line) is not { } start || start >= List.Count) return null;

		for (LuaNode? token = List[start]; token is not null; token = token.Parent) {
			if (token.IsScopeController && token.Contains(line, column)) return token;
		}

		return null;
	}

	public List<LuaNode> TokensOnLine(int line, int column, LuaNodeType? type = null) {
		List<LuaNode> tokens = [];
		List<LuaNode>? references = type is { } filter ? Of(filter)?.List : List;
		LineIndex? search = type is { } kind ? Of(kind)?.Lines : Lines;

		if (references is null || search?.StartFor(line) is not { } start) return tokens;

		bool reached = false;

		for (int i = start; i < references.Count; i++) {
			LuaNode token = references[i];

			if (token.StartLine - 1 == line || (!token.IsScopeController && token.Contains(line, column))) {
				reached = true;
				tokens.Add(token);
			}
			else if (reached || token.EndLine - 1 > line) break;
		}

		return tokens;
	}

	public LuaNode? TokenAt(int line, int column, LuaNodeType? type = null) {
		List<LuaNode> tokens = TokensOnLine(line, column, type);

		if (tokens.Count == 0) return null;

		HashSet<LuaNode> seen = [.. tokens];
		List<LuaNode> nested = [];

		foreach (LuaNode token in tokens) Nested(token, nested, seen);

		tokens.AddRange(nested);

		LuaNode? found = null;

		foreach (LuaNode token in tokens) {
			if (found is null) {
				if (token.Contains(line, column)) found = token;

				continue;
			}

			if (token.Contains(line, column) && found.Contains(token)) found = token;
		}

		return found;
	}

	private static void Nested(LuaNode token, List<LuaNode> into, HashSet<LuaNode> seen) {
		if (token.IsScopeController || !token.HasChildren) return;

		List<LuaNode> children = [];
		token.Children(children);

		foreach (LuaNode child in children) {
			if (seen.Add(child)) into.Add(child);

			Nested(child, into, seen);
		}
	}
}

public sealed partial class TokenExtractor
{
	private static readonly Dictionary<string, string> NetworkVarTypes = new(StringComparer.Ordinal) { ["Int"] = "number", ["Bool"] = "boolean", ["Float"] = "number", ["String"] = "string" };

	[GeneratedRegex(@"([^\s\\\/]+?)(?:(?:\/|\\)[^\s\\\/]+?)?\.lua$")]
	private static partial Regex NetworkVarContext();

	private readonly ParsedFile _file;
	private readonly GLuaCatalog _catalog;
	private readonly FileTokenData _data;
	private readonly bool _excluded;
	private readonly HashSet<LuaNode> _visited = [];

	private TokenExtractor(ParsedFile file, GLuaCatalog catalog, bool excluded) {
		_file = file;
		_catalog = catalog;
		_data = file.Data;
		_excluded = excluded;
	}

	public static ParsedFile Extract(string path, LuaNode chunk, GLuaCatalog catalog, bool excluded) {
		ParsedFile file = new() { Path = path, Chunk = chunk, Data = new FileTokenData() };
		new TokenExtractor(file, catalog, excluded).Traverse(chunk, null);
		return file;
	}

	private void Traverse(LuaNode token, LuaNode? parent) {
		if (!_visited.Add(token)) return;

		Parse(token, parent);

		if (!token.HasChildren) return;

		List<LuaNode> children = [];
		token.Children(children);

		foreach (LuaNode child in children) Traverse(child, token);
	}

	private static void Scope(LuaNode token, string name, LuaNode value) => (token.Scope ??= [with(StringComparer.Ordinal)])[name] = value;

	private void Parse(LuaNode token, LuaNode? parent) {
		if (parent is not null) token.Parent = parent;

		if (!_file.Types.TryGetValue(token.Type, out TypedTokens? typed)) _file.Types[token.Type] = typed = new TypedTokens();

		_file.List.Add(token);
		typed.List.Add(token);

		if (token.Type != LuaNodeType.Chunk) {
			_file.Lines.Note(_file.List.Count - 1, token.StartLine - 1);
			typed.Lines.Note(typed.List.Count - 1, token.StartLine - 1);
		}

		token.Path = _file.Path;

		switch (token.Type) {
			case LuaNodeType.ForGenericStatement:
				foreach (LuaNode variable in token.Variables!) Scope(token, variable.Name!, variable);

				break;
			case LuaNodeType.ForNumericStatement:
				Scope(token, token.Variable!.Name!, token.Variable);
				break;
			case LuaNodeType.FunctionDeclaration:
				FunctionDeclaration(token);
				break;
			case LuaNodeType.AssignmentStatement:
				Assignment(token);
				break;
			case LuaNodeType.LocalStatement:
				for (LuaNode? scope = token; scope is not null; scope = scope.Parent) {
					if (!scope.IsScopeController) continue;

					foreach (LuaNode variable in token.Variables!) {
						if (variable.IsLocal) Scope(scope, variable.Name!, variable);
					}

					break;
				}

				break;
			case LuaNodeType.CallExpression:
				Call(token);
				break;
		}
	}

	private static bool InScope(LuaNode? start, string name) {
		for (LuaNode? scope = start; scope is not null; scope = scope.Parent) {
			if (scope.Scope?.ContainsKey(name) == true) return true;
		}

		return false;
	}

	private static bool BaseIsLocal(LuaNode? node) {
		for (; node is not null; node = node.HasBase ? node.Base : null) {
			if (node.IsLocal) return true;
		}

		return false;
	}

	private void FunctionDeclaration(LuaNode token) {
		if (token.Identifier is { } identifier) {
			Traverse(identifier, token);
			(List<string> full, _) = TokenAnalyzer.QualifyMemberExpression(identifier);

			if (full.Count > 0) {
				string name = string.Concat(full);
				bool isLocal = token.IsLocal || (identifier.Type == LuaNodeType.MemberExpression && BaseIsLocal(identifier));

				if (!isLocal && full.Count == 1) isLocal = InScope(token.Parent, name);

				if (isLocal) {
					Scope(token, name, token);

					if (token.Parent is { IsScopeController: true } parent) Scope(parent, name, token);
				}
				else AddGlobal(token, full, name, isFunction: true);
			}
		}

		foreach (LuaNode parameter in token.Parameters!) {
			if (parameter.Name is not { } name || name == "self") continue;

			Scope(token, name, parameter);
		}
	}

	private void Assignment(LuaNode token) {
		List<LuaNode> variables = token.Variables!;
		List<LuaNode> init = token.Init!;

		for (int i = 0; i < variables.Count; i++) {
			if (i >= init.Count || init[i].Type == LuaNodeType.NilLiteral || variables[i].IsLocal) continue;

			Traverse(init[i], token);
			Traverse(variables[i], token);

			(List<string> full, _) = TokenAnalyzer.QualifyMemberExpression(variables[i]);

			if (full.Count == 0 || full[0] == "self") continue;

			string name = string.Concat(full);

			if (BaseIsLocal(variables[i]) || InScope(token, name)) continue;

			AddGlobal(token, full, name, isFunction: false);
		}
	}

	private void Call(LuaNode token) {
		if (token.Base is { IsLocal: true }) return;

		List<string>? split = TokenAnalyzer.FullFunctionCall(token);

		if (split is null || split.Count == 0) return;

		if (split.Count == 1) {
			if (_catalog.Docs.TryGetValue("GLOBAL:" + split[0], out WikiDoc? global)) (token.Docs ??= []).Add(global);

			return;
		}

		string full = string.Concat(split);

		if (split.Count < 3 || split[^2] != ":") {
			if (_catalog.Docs.TryGetValue("FUNCTION:" + full, out WikiDoc? function)) (token.Docs ??= []).Add(function);
		}
		else if (_catalog.SignatureMetaFunctions.TryGetValue(split[^1], out List<WikiDoc>? meta)) (token.Docs ??= []).AddRange(meta);

		if (token.Parent?.Type == LuaNodeType.AssignmentStatement) return;

		if (TokenAnalyzer.FunctionArguments(token) is not { } arguments) return;

		switch (full) {
			case "util.AddNetworkString":
				if (arguments.Count >= 1 && TokenAnalyzer.StringLiteral(arguments[0]) is { } added) AddNetworkString(null, added, reference: false);

				break;
			case "net.Receive":
				if (arguments.Count >= 1 && TokenAnalyzer.StringLiteral(arguments[0]) is { } received) AddNetworkString(token, received, reference: false);

				break;
			case "net.Start":
				if (arguments.Count >= 1 && TokenAnalyzer.StringLiteral(arguments[0]) is { } started) AddNetworkString(token, started, reference: true);

				break;
			default:
				if (split.Count >= 2 && split[^2] == ":" && split[^1] == "NetworkVar" && arguments.Count >= 3 && TokenAnalyzer.StringLiteral(arguments[0]) is { } type && TokenAnalyzer.StringLiteral(arguments[2]) is { } name) AddNetworkVar(token, NetworkVarTypes.GetValueOrDefault(type, type), name);

				break;
		}
	}

	private static void Push(Dictionary<string, List<LuaNode>> map, string key, LuaNode token) {
		if (!map.TryGetValue(key, out List<LuaNode>? list)) map[key] = list = [];

		list.Add(token);
	}

	private void AddGlobal(LuaNode token, List<string> call, string name, bool isFunction) {
		if (_excluded || _catalog.Struct.ContainsKey(call[0])) return;

		GLuaItem item = new(call[0], isFunction ? "Function" : "Variable") { Detail = "(global)", NoDoc = true };

		if (!isFunction || call.Count == 1) {
			_data.Globals.AddUnique(item);

			if (isFunction) _data.GlobalFunctions.AddUnique(item);
		}

		for (int pass = 0; pass == 0 || (isFunction && pass == 1); pass++) {
			GLuaList list = pass == 0 ? _data.Globals : _data.GlobalFunctions;
			string label = string.Empty;

			for (int j = 0; j <= call.Count; j++) {
				if (j == call.Count || call[j] is "." or ":") {
					string key = call[j - 1];

					if (!list.Has(key)) {
						if (!isFunction || j == call.Count) list.AddUnique(item.With(label, key));

						list.Child(key, list.IsIncomplete);
					}

					list = list.Find(key)!;
				}

				if (j != call.Count) label += call[j];
			}
		}

		if (call.Count == 1) Push(_data.G, name, token);

		Push(_data.GlobalTokens, name, token);
	}

	private void AddNetworkVar(LuaNode token, string type, string name) {
		if (_excluded) return;

		string getter = "Get" + name;
		string setter = "Set" + name;

		Push(_data.DefinitionMetaFunctions, getter, token);
		Push(_data.DefinitionMetaFunctions, setter, token);

		Match context = NetworkVarContext().Match(_file.Path);
		string owner = context.Success ? context.Groups[1].Value : "Entity";
		WikiFlags realm = WikiFlags.Client | WikiFlags.Server | WikiFlags.HasClient | WikiFlags.HasServer;
		WikiDoc getDoc = new() { Flags = realm, Returns = [new WikiDoc { Type = type }], NetworkVar = type, Search = owner + ":Get" + name };
		WikiDoc setDoc = new() { Flags = realm, Arguments = [new WikiDoc { Type = type, Name = name }], NetworkVar = type, Search = owner + ":Set" + name };

		Add(_data.SignatureMetaFunctions, getter, getDoc);
		Add(_data.SignatureMetaFunctions, setter, setDoc);

		_data.MetaFunctions.AddUnique(new GLuaItem(owner + ":Get" + name, "Method") { InsertText = getter, Doc = getDoc });
		_data.MetaFunctions.AddUnique(new GLuaItem(owner + ":Set" + name, "Method") { InsertText = setter, Doc = setDoc });
	}

	private static void Add(Dictionary<string, List<WikiDoc>> map, string key, WikiDoc doc) {
		if (!map.TryGetValue(key, out List<WikiDoc>? list)) map[key] = list = [];

		list.Add(doc);
	}

	private void AddNetworkString(LuaNode? token, string networkString, bool reference) {
		if (_excluded) return;

		_data.NetworkStrings.AddUnique(new GLuaItem(networkString, "Event") { NoDoc = true });

		if (token is null) return;

		Push(reference ? _data.ReferenceNetworkStrings : _data.DefinitionNetworkStrings, networkString, token);
	}
}

public sealed class FileTokenData
{
	public Dictionary<string, List<LuaNode>> GlobalTokens { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, List<LuaNode>> G { get; } = [with(StringComparer.Ordinal)];

	public GLuaList Globals { get; } = new();

	public GLuaList GlobalFunctions { get; } = new();

	public GLuaList MetaFunctions { get; } = new();

	public GLuaList NetworkStrings { get; } = new();

	public Dictionary<string, List<WikiDoc>> SignatureMetaFunctions { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, List<LuaNode>> DefinitionMetaFunctions { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, List<LuaNode>> DefinitionNetworkStrings { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, List<LuaNode>> ReferenceNetworkStrings { get; } = [with(StringComparer.Ordinal)];

	public void Merge(FileTokenData other) {
		Append(GlobalTokens, other.GlobalTokens);
		Append(G, other.G);
		Globals.Merge(other.Globals);
		GlobalFunctions.Merge(other.GlobalFunctions);
		MetaFunctions.Merge(other.MetaFunctions);
		NetworkStrings.Merge(other.NetworkStrings);

		foreach ((string key, List<WikiDoc> docs) in other.SignatureMetaFunctions) {
			if (!SignatureMetaFunctions.TryGetValue(key, out List<WikiDoc>? list)) SignatureMetaFunctions[key] = list = [];

			list.AddRange(docs);
		}

		Append(DefinitionMetaFunctions, other.DefinitionMetaFunctions);
		Append(DefinitionNetworkStrings, other.DefinitionNetworkStrings);
		Append(ReferenceNetworkStrings, other.ReferenceNetworkStrings);
	}

	private static void Append(Dictionary<string, List<LuaNode>> into, Dictionary<string, List<LuaNode>> from) {
		foreach ((string key, List<LuaNode> tokens) in from) {
			if (!into.TryGetValue(key, out List<LuaNode>? list)) into[key] = list = [];

			list.AddRange(tokens);
		}
	}
}
