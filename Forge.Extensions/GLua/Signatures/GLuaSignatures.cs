using System.Text.RegularExpressions;

using Forge.Extensions.GLua.Completion;
using Forge.Extensions.GLua.Index;
using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua.Signatures;

public sealed class GLuaParameter(string label, string? documentation)
{
	public string Label { get; } = label;

	public string? Documentation { get; } = documentation;

	public string? Enum { get; init; }

	public GLuaSignatureHelp? CallbackSignatures { get; set; }
}

public sealed class GLuaSignature(string label, string? documentation)
{
	public string Label { get; } = label;

	public string? Documentation { get; } = documentation;

	public int ActiveParameter { get; set; }

	public List<GLuaParameter> Parameters { get; } = [];
}

public sealed class GLuaSignatureHelp
{
	public List<GLuaSignature> Signatures { get; } = [];

	public int ActiveSignature { get; init; }
}

public sealed partial class GLuaSignatures(GLuaCatalog catalog, GLuaIndex index)
{
	private sealed class Abort : Exception;

	private static readonly WikiDoc Self = new() { Name = "self" };

	[GeneratedRegex(@"^(.+)(:|\.)(.+?)$")]
	private static partial Regex CallType();

	public static string SignatureString(IEnumerable<WikiDoc> arguments) => string.Join(", ", arguments.Select(TypeSignature));

	public static string TypeSignature(WikiDoc argument) {
		if (argument.Name is { } name && argument.Type is { } type && !string.Equals(name, type, StringComparison.OrdinalIgnoreCase)) return name + ": " + (type == "vararg" ? "..." : type);

		return argument.Type is { } only ? only == "vararg" ? "..." : only : argument.Name ?? "UNKNOWN";
	}

	private static void Push(bool hasSelf, int activeParameter, List<GLuaSignature> signatures, WikiDoc docs, bool callback, int activeCallbackParameter, string? callbackDocumentation) {
		WikiDoc[]? arguments = docs.Arguments ?? docs.Callback;
		string? documentation = callbackDocumentation ?? (docs.Search is { } search ? WikiMarkdown.Resolve(docs, search, compact: true) : null);

		if (arguments is null) {
			if (!hasSelf || activeParameter > 0) return;

			GLuaSignature self = new(SignatureString([Self]), documentation) { ActiveParameter = 0 };
			self.Parameters.Add(new GLuaParameter(TypeSignature(Self), null));
			signatures.Add(self);
			return;
		}

		int count = arguments.Length + (hasSelf ? 1 : 0);
		int position = Math.Min(activeParameter, count - 1);

		if (activeParameter >= count && count - 1 >= arguments.Length) throw new Abort();

		bool withinCount = activeParameter < count || arguments[count - 1].Type == "vararg";

		if (!withinCount) return;

		if (callback && position >= arguments.Length) throw new Abort();

		if (callback && arguments[position].Callback is not null && arguments[position].Type != "function") return;

		GLuaSignature signature = new(SignatureString(hasSelf ? [Self, .. arguments] : arguments), documentation) { ActiveParameter = position };

		if (!callback && hasSelf) signature.Parameters.Add(new GLuaParameter(TypeSignature(Self), null));

		for (int i = 0; i < count - (hasSelf ? 1 : 0); i++) {
			WikiDoc argument = arguments[i];
			string? parameterDocumentation = argument.Description.IsEmpty ? null : WikiMarkdown.Resolve(argument) + (signature.Documentation is not null ? "\n\n---" : "");
			GLuaParameter parameter = new(TypeSignature(argument), parameterDocumentation) { Enum = argument.Enum };

			if (callback && position == i) {
				GLuaSignatureHelp nested = new();
				Push(hasSelf, activeCallbackParameter, nested.Signatures, argument, false, 0, signature.Documentation);
				parameter.CallbackSignatures = nested;
			}

			signature.Parameters.Add(parameter);
		}

		signatures.Add(signature);
	}

	private static void PushAll(bool hasSelf, int activeParameter, List<GLuaSignature> signatures, IEnumerable<WikiDoc> docs, bool callback, int activeCallbackParameter) {
		foreach (WikiDoc doc in docs) Push(hasSelf, activeParameter, signatures, doc, callback, activeCallbackParameter, null);
	}

	public GLuaSignatureHelp? Provide(string cursor) {
		try {
			return Resolve(cursor);
		}
		catch (Abort) {
			return null;
		}
	}

	private GLuaSignatureHelp? Resolve(string cursor) {
		LineTokenizer tokenized = new(cursor);

		if (tokenized.InvalidLua || tokenized.OpenParentheses.Count == 0) return null;

		if (tokenized.OpenParentheses[^1] is not { } call) return null;

		int activeCallbackParameter = 0;
		bool callback = false;

		if (tokenized.OpenParentheses.Count > 1 && call[0] == "function") {
			callback = true;
			activeCallbackParameter = call.Count - 1;

			if (tokenized.OpenParentheses[^2] is not { } outer) return null;

			call = outer;
		}

		int activeParameter = call.Count - 1;
		Match parsed = CallType().Match(call[0]);
		List<GLuaSignature> signatures = [];
		FileTokenData compiled = index.Compiled;

		for (int i = 0; i <= 1; i++) {
			Dictionary<string, WikiDoc> globals = i == 0 ? catalog.SignatureGlobals : Empty;
			Dictionary<string, WikiDoc> functions = i == 0 ? catalog.SignatureFunctions : Empty;
			Dictionary<string, List<WikiDoc>> metaFunctions = i == 0 ? catalog.SignatureMetaFunctions : compiled.SignatureMetaFunctions;

			if (!parsed.Success) {
				if (globals.TryGetValue(call[0], out WikiDoc? global)) PushAll(false, activeParameter, signatures, [global], callback, activeCallbackParameter);

				continue;
			}

			string fullCall = parsed.Value;
			string owner = parsed.Groups[1].Value;
			string separator = parsed.Groups[2].Value;
			string member = parsed.Groups[3].Value;

			if (separator == ":" && catalog.Hook.Has(owner)) {
				if (metaFunctions.TryGetValue(fullCall, out List<WikiDoc>? hooks)) PushAll(false, activeParameter, signatures, hooks, callback, activeCallbackParameter);

				continue;
			}

			if (functions.TryGetValue(fullCall, out WikiDoc? function)) {
				if (callback && fullCall == "hook.Add" && call.Count > 1 && call[1].StartsWith('"') && metaFunctions.TryGetValue("GM:" + call[1][1..^1], out List<WikiDoc>? hook)) {
					bool hasSelf = call.Count <= 2 || !call[2].StartsWith('"');
					PushAll(hasSelf, activeCallbackParameter, signatures, hook, false, 0);
					continue;
				}

				PushAll(false, activeParameter, signatures, [function], callback, activeCallbackParameter);
				continue;
			}

			if (metaFunctions.TryGetValue(member, out List<WikiDoc>? meta)) PushAll(false, activeParameter, signatures, meta, callback, activeCallbackParameter);
		}

		if (signatures.Count == 0) return null;

		if (callback && activeParameter < signatures[0].Parameters.Count && signatures[0].Parameters[activeParameter].CallbackSignatures is { } nested) return nested;

		GLuaSignatureHelp help = new();
		help.Signatures.AddRange(signatures);
		return help;
	}

	private static readonly Dictionary<string, WikiDoc> Empty = [];
}
