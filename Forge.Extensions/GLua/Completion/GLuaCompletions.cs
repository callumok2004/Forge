using System.Text.RegularExpressions;

using Forge.Extensions.GLua.Index;
using Forge.Extensions.GLua.Parsing;
using Forge.Extensions.GLua.Signatures;
using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua.Completion;

public sealed record CompletionContext(string Path, string Text, int Line, int Character, string Term, char Trigger, bool IsTriggerCharacter);

public sealed partial class GLuaCompletions(GLuaCatalog catalog, GLuaIndex index, GLuaSignatures signatures, Func<string?> workspace)
{
	[GeneratedRegex(@"(?<!\B|:|\.)(?:(function)\s+)?([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*?)(?:(\.|:)(?:[A-Za-z_][A-Za-z0-9_]*)?)?$")]
	public static partial Regex FunctionCompletions();

	[GeneratedRegex(@"^(?=([A-Za-z0-9_]*[A-Za-z_]))\1((?::|\.)(?:[A-Za-z0-9_]*[A-Za-z_])?)?(\s+noitcnuf\s+lacol)?")]
	private static partial Regex GlobalCompletions();

	[GeneratedRegex(@"^[\t\t\f\v]*(local +)?(?:function +([A-Za-z_][A-Za-z0-9_]*)?|(funct?i?o?n?))((?::|\.)(?:[A-Za-z_][A-Za-z0-9_]*)?)?$")]
	private static partial Regex FunctionDeclarationCompletions();

	[GeneratedRegex(@"hook\.(Add|Remove|GetTable|Run|Call)\s*\((?:[""']|\[=*\[)$")]
	private static partial Regex HookCompletions();

	[GeneratedRegex(@"vgui\.Create\(\s*(?:[""']|\[=*\[)$")]
	private static partial Regex VguiCreate();

	[GeneratedRegex(@"net\.(?:Receive|Start)\(\s*(?:[""']|\[=*\[)$")]
	private static partial Regex NetMessage();

	[GeneratedRegex(@"(?:(?:include|AddCSLuaFile|CompileFile)\s*\(\s*(?:[""']|\[=*\[)(?:lua\/)?|lua\/)([^\s]+\/)?$")]
	private static partial Regex LuaCompletions();

	[GeneratedRegex(@"\b(?:(?:(?:(?::|\.)(?:SetImage|SetMaterial))|Material|surface\.GetTextureID)\s*\(\s*(?:[""']|\[=*\[)(?:materials\/)?|materials\/)([^\s]+\/)?$")]
	private static partial Regex MaterialCompletions();

	[GeneratedRegex(@"\b(?:(?:(?:(?::|\.)(?:EmitSound|StopSound|StartLoopingSound))|Sound|SoundDuration|sound\.Play(?:File)?|surface\.PlaySound|util\.PrecacheSound)\s*\(\s*(?:[""']|\[=*\[)(?:sound\/)?|sound\/)([^\s]+\/)?")]
	private static partial Regex SoundCompletions();

	[GeneratedRegex(@"\b(?:(?:(?:(?::|\.)(?:SetModel|SetWeaponModel))|(?<![^\s;=+\/\-\*,\)\({}])Model|IsUselessModel|ClientsideModel|CreatePhysCollidesFromModel|ents\.FindByModel|NumModelSkins|player_manager\.TranslateToPlayerModelName|util\.(?:PrecacheModel|GetModelInfo|GetModelMeshes|IsModelLoaded|IsValidModel|IsValidProp)|ents\.CreateClientProp)\s*\(\s*(?:[""']|\[=*\[)|(models\/))([^\s]+\/)?$")]
	private static partial Regex ModelCompletions();

	[GeneratedRegex(@"(?:(""|')(?:(?:\\\1|\\\\|.)*?)(\1|$))|(?:\[(=*)\[(?:[\s\S]*?)(\]\3\]|$))")]
	public static partial Regex InsideLuaString();

	private GLuaList? _models;
	private GLuaList? _materials;
	private GLuaList? _sounds;
	private readonly Dictionary<string, WikiDoc> _images = [with(StringComparer.OrdinalIgnoreCase)];

	public const string TriggerCharacters = "/\"'[.:(, ";

	private delegate GLuaList? Provider(CompletionContext context);

	public IReadOnlyList<GLuaItem> Provide(CompletionContext context) {
		List<GLuaItem> items = [];

		Run(items, context, FilePaths, true, "/\"'[");
		Run(items, context, Strings, true, "\"'[");
		Run(items, context, Specialized, false, ".:(");
		Run(items, context, Arguments, false, "(, ");
		Run(items, context, Generalized, false, "");
		Run(items, context, Scoped, false, "");
		Run(items, context, Tokens, false, ".:(");
		Run(items, context, Globals, false, "");

		return items;
	}

	public bool AnyIncomplete { get; private set; }

	private void Run(List<GLuaItem> items, CompletionContext context, Provider provider, bool allowInStrings, string triggers) {
		if (context.IsTriggerCharacter) {
			if (!triggers.Contains(context.Trigger)) return;
		}
		else if (triggers.Length > 0 && (context.Term.Length == 0 || !triggers.Contains(context.Term[^1]))) return;

		if (!allowInStrings && IsTermInsideString(context.Character, context.Term)) return;

		if (provider(context) is not { } list) return;

		AnyIncomplete |= list.IsIncomplete;
		items.AddRange(list.Items);
	}

	public void Reset() => AnyIncomplete = false;

	public static bool IsTermInsideString(int character, string term) {
		foreach (Match match in InsideLuaString().Matches(term)) {
			if (character >= match.Index && character <= match.Index + match.Length) return true;
		}

		return false;
	}

	private GLuaList? Scoped(CompletionContext context) {
		if (index.Get(context.Path, context.Text)?.FindChunkAt(context.Line, context.Character) is not { } chunk) return null;

		GLuaList scoped = new();
		Dictionary<LuaNode, string> visited = [];

		for (LuaNode? token = chunk; token is not null; token = token.Parent) {
			if (visited.TryAdd(token, string.Empty) && token.Scope is { } scope) {
				foreach ((string name, LuaNode scopeToken) in scope) {
					if (scopeToken.EndLine - 1 > context.Line || (scopeToken.EndLine - 1 == context.Line && scopeToken.EndColumn > context.Character)) continue;

					string kind = scopeToken.Type == LuaNodeType.FunctionDeclaration ? "Function" : "Variable";

					if (visited.TryGetValue(scopeToken, out string? seen) && seen == kind) continue;

					visited[scopeToken] = kind;
					scoped.Items.Add(new GLuaItem(name, kind) { NoDoc = true });
				}
			}
		}

		return scoped.Items.Count > 0 ? scoped : null;
	}

	private GLuaList? Arguments(CompletionContext context) {
		if (context.IsTriggerCharacter && context.Trigger == ' ' && !context.Term.EndsWith(", ", StringComparison.Ordinal)) return null;

		if (signatures.Provide(context.Term) is not { Signatures.Count: > 0 } help) return null;

		GLuaSignature active = help.Signatures[help.ActiveSignature];

		return active.ActiveParameter < active.Parameters.Count && active.Parameters[active.ActiveParameter].Enum is { } family && catalog.EnumFamily.TryGetValue(family, out GLuaList? list) ? list : null;
	}

	private GLuaList? Strings(CompletionContext context) {
		if (VguiCreate().IsMatch(context.Term)) return catalog.Panel;

		if (NetMessage().IsMatch(context.Term)) return index.Compiled.NetworkStrings;

		Match hook = HookCompletions().Match(context.Term);

		if (!hook.Success) return null;

		return hook.Groups[1].Value == "Call" ? catalog.Hook : catalog.Hook.Find("GM");
	}

	private GLuaList? Generalized(CompletionContext context) {
		string term = context.Term;

		if (term.Length >= 3) {
			Match enumMatch = GLuaCatalog.EnumCompletions().Match(term);

			if (enumMatch.Success && !enumMatch.Groups[1].Success && enumMatch.Groups[2].Length > 0) {
				if (!enumMatch.Groups[4].Success) return catalog.Enum;

				if (catalog.EnumFamilySub.TryGetValue(enumMatch.Groups[3].Value, out GLuaList? sub)) return sub;
			}
		}

		Match declaration = FunctionDeclarationCompletions().Match(term);

		if (declaration.Success && !declaration.Groups[1].Success) {
			GLuaList decl = new(true);

			foreach (GLuaItem item in catalog.FunctionDecl.Items) {
				GLuaItem ranged = item.With(item.Label, item.InsertText);
				ranged.ReplaceStartColumn = declaration.Index + 1;
				decl.Items.Add(ranged);
			}

			string name = declaration.Groups[2].Value;

			if (!declaration.Groups[3].Success && (name.Length == 0 || name.ToUpperInvariant() != name)) decl.Items.InsertRange(0, catalog.GenericFunc.Items);

			return decl;
		}

		if (Specialized(context) is not null) return null;

		char[] reversed = term.ToCharArray();
		Array.Reverse(reversed);
		Match global = GlobalCompletions().Match(new string(reversed));

		if (!global.Success || global.Groups[3].Success) return null;

		if (global.Groups[1].Length == 0) return catalog.Generic;

		return global.Groups[2].Success && global.Groups[2].Length > 0 ? catalog.MetaFunc : catalog.Global;
	}

	private GLuaList? Specialized(CompletionContext context) {
		Match match = FunctionCompletions().Match(context.Term);

		if (!match.Success) return null;

		string? functionContext = match.Groups[1].Success ? match.Groups[1].Value : null;
		string name = match.Groups[2].Value.TrimEnd('.', ':');
		string? call = match.Groups[3].Success ? match.Groups[3].Value : null;

		if (call == ":" || (call == "." && functionContext == "function")) {
			if (catalog.Hook.Find(name == "GAMEMODE" ? "GM" : name) is { } hooks) return hooks;
		}

		if (call == ".") {
			if (catalog.Struct.TryGetValue(name == "GAMEMODE" ? "GM" : name, out GLuaList? fields)) return fields;

			if (catalog.LibraryFunc.TryGetValue(name, out GLuaList? library)) {
				if (library is not null) return library;

				return functionContext == "function" ? catalog.MetaFunc : null;
			}
		}

		if (call is not null && GlobalTable(name, call) is { } table) return table;

		return call == ":" ? catalog.MetaFunc : null;
	}

	public GLuaList? GlobalTable(string name, string call) {
		string[] keys = (name + call).Replace(':', '.').Split('.');

		if (keys.Length <= 1) return null;

		bool found = false;
		FileTokenData compiled = index.Compiled;
		GLuaList items = call == ":" ? compiled.GlobalFunctions : compiled.Globals;

		for (int i = 0; i < keys.Length; i++) {
			if (items.Find(keys[i]) is { } child) {
				items = child;
				found = true;
			}
			else if (i != keys.Length - 1) return null;
		}

		return found ? items : null;
	}

	private GLuaList? Globals(CompletionContext context) {
		Match match = FunctionCompletions().Match(context.Term);
		return match.Success && !match.Groups[3].Success ? index.Compiled.Globals : null;
	}

	private GLuaList? Tokens(CompletionContext context) {
		Match match = FunctionCompletions().Match(context.Term);

		if (!match.Success) return null;

		string? functionContext = match.Groups[1].Success ? match.Groups[1].Value : null;
		string name = match.Groups[2].Value;
		string? call = match.Groups[3].Success ? match.Groups[3].Value : null;

		if (call is not null && GlobalTable(name, call) is not null) return null;

		if ((call == ":" || (call == "." && functionContext == "function")) && catalog.Hook.Has(name == "GAMEMODE" ? "GM" : name)) return null;

		return call == ":" ? index.Compiled.MetaFunctions : null;
	}

	private static IEnumerable<string> Relative(string root, string folder, string pattern) {
		string start = Path.Combine(root, folder);

		if (!Directory.Exists(start)) return [];

		try {
			return Directory.EnumerateFiles(start, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return [];
		}
	}

	private static GLuaList? Walk(GLuaList? root, IEnumerable<string> path) {
		foreach (string segment in path) {
			if (root?.Find(segment) is not { } next) return null;

			root = next;
		}

		return root;
	}

	private static string[] Segments(string text) => [.. text.Split('/').Where(static segment => segment.Length > 0).Select(static segment => segment + "/")];

	private static GLuaList Tree(IEnumerable<string> files, string? strip, string folderSort, string fileSort, Action<GLuaItem, string>? onFile = null) {
		GLuaList root = new();

		foreach (string relative in files) {
			string trimmed = strip is not null && relative.StartsWith(strip, StringComparison.Ordinal) ? relative[strip.Length..] : relative;
			string[] parts = trimmed.Split('/');
			GLuaList folder = root;

			for (int j = 0; j < parts.Length - 1; j++) {
				string key = parts[j] + "/";

				if (!folder.Has(key)) folder.Items.Add(new GLuaItem(key, "Folder") { NoDoc = true, InsertText = parts[j], SortText = folderSort });

				folder = folder.Child(key);
			}

			GLuaItem item = new(parts[^1], "File") { SortText = fileSort, NoDoc = true };
			onFile?.Invoke(item, relative);
			folder.Items.Add(item);
		}

		return root;
	}

	private static GLuaList? Combine(GLuaList? builtIn, GLuaList? workspace) {
		if (builtIn is null || workspace is null) return workspace ?? builtIn;

		GLuaList combined = new();
		combined.Items.AddRange(builtIn.Items);
		combined.Items.AddRange(workspace.Items);
		return combined;
	}

	private GLuaList? FilePaths(CompletionContext context) {
		string term = context.Term;
		string? root = workspace();

		Match lua = LuaCompletions().Match(term);

		if (lua.Success) {
			if (root is null) return new GLuaList();

			string sub = lua.Groups[1].Success ? lua.Groups[1].Value : string.Empty;
			GLuaList list = new();

			foreach (string relative in Relative(root, Path.Combine("lua", sub), "*.lua")) {
				string insert = relative["lua/".Length..];

				if (sub.Length > 0) insert = insert[Math.Min(sub.Length, insert.Length)..];

				list.Items.Add(new GLuaItem(relative, "File") { NoDoc = true, InsertText = insert });
			}

			return list;
		}

		Match models = ModelCompletions().Match(term);

		if (models.Success) {
			string[] path = Segments((models.Groups[1].Success ? "models/" : "") + (models.Groups[2].Success ? models.Groups[2].Value : ""));

			if (context.IsTriggerCharacter && root is not null) {
				List<string> files = [.. Relative(root, "models", "*.mdl")];
				_models = files.Count > 0 ? Tree(files, null, "0", "1") : null;
			}

			return Walk(_models, path);
		}

		Match materials = MaterialCompletions().Match(term);

		if (materials.Success) {
			string[] path = Segments(materials.Groups[1].Success ? materials.Groups[1].Value : "");

			if (context.IsTriggerCharacter && root is not null) {
				List<string> files = [.. Relative(root, "materials", "*.png"), .. Relative(root, "materials", "*.vmt")];
				_materials = files.Count > 0 ? Tree(files, "materials/", "0", "1", (item, relative) => {
					item.DocTag = relative;
					item.NoDoc = false;

					if (!relative.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase)) lock (_images) _images[relative] = new WikiDoc { RawImage = Path.Combine(root, relative) };
				}) : null;
			}

			return Walk(_materials, path);
		}

		Match sound = SoundCompletions().Match(term);

		if (!sound.Success) return null;

		string[] soundPath = Segments(sound.Groups[1].Success ? sound.Groups[1].Value : "");
		GLuaList? builtIn = Walk(catalog.SoundsAll, soundPath);

		if (context.IsTriggerCharacter && root is not null) {
			List<string> files = [.. Relative(root, "sound", "*.*")];
			_sounds = files.Count > 0 ? Tree(files, "sound/", "00", "01") : null;
		}

		return _sounds is null ? builtIn : Combine(builtIn, Walk(_sounds, soundPath));
	}

	public WikiDoc? Image(string tag) {
		lock (_images) return _images.GetValueOrDefault(tag);
	}
}
