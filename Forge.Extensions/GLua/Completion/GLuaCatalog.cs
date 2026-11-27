using System.Text.Json;
using System.Text.RegularExpressions;

using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua.Completion;

public sealed partial class GLuaCatalog
{
	public Dictionary<string, WikiDoc> Docs { get; } = [with(StringComparer.Ordinal)];

	public GLuaList Generic { get; } = new(true);

	public GLuaList GenericFunc { get; } = new(true);

	public GLuaList Enum { get; } = new(true);

	public GLuaList Global { get; } = new(true);

	public GLuaList Panel { get; } = new(true);

	public GLuaList FunctionDecl { get; } = new(true);

	public GLuaList MetaFunc { get; } = new();

	public GLuaList Hook { get; } = new();

	public Dictionary<string, GLuaList> EnumFamily { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, GLuaList> EnumFamilySub { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, GLuaList?> LibraryFunc { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, GLuaList> Struct { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, WikiDoc> SignatureGlobals { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, WikiDoc> SignatureFunctions { get; } = [with(StringComparer.Ordinal)];

	public Dictionary<string, List<WikiDoc>> SignatureMetaFunctions { get; } = [with(StringComparer.Ordinal)];

	public GLuaList SoundsAll { get; } = new();

	public Dictionary<string, GLuaList> SoundsByGame { get; } = [with(StringComparer.Ordinal)];

	[GeneratedRegex(@"((?:function|local)\s+)?(?<!\.|:)\b(([A-Z][A-Z_0-9]*)(?:(\.)(?:[A-Z][A-Z_0-9]*)?)*)$")]
	public static partial Regex EnumCompletions();

	public static GLuaCatalog Build(WikiData wiki, string soundsPath) {
		GLuaCatalog catalog = new();
		catalog.AddWiki(wiki);
		catalog.LoadSounds(soundsPath);

		return catalog;
	}

	private GLuaItem? Create(string? tag, string label, string kind, WikiDoc? definition, string? displayLabel = null, string? insertText = null) {
		GLuaItem item = new(displayLabel ?? label, kind) { InsertText = insertText ?? label };

		if (displayLabel is not null) {
			item.FilterText = label;
			item.SortText = label;
		}
		else {
			item.FilterText = item.InsertText;
			item.SortText = item.InsertText;
		}

		if (definition is null) return item;

		if (definition.Has(WikiFlags.Deprecated)) item.Deprecated = true;

		if (tag is null) return item;

		definition.Tag = tag;

		if (definition.Search is not { } search) return item;

		string docTag = tag + ":" + search;

		if (Docs.ContainsKey(docTag)) return null;

		item.DocTag = docTag;
		Docs[docTag] = definition;
		RegisterSignature(item, tag, definition);
		return item;
	}

	private void AddMeta(string name, WikiDoc definition) {
		if (!SignatureMetaFunctions.TryGetValue(name, out List<WikiDoc>? list)) SignatureMetaFunctions[name] = list = [];

		list.Add(definition);
	}

	private void RegisterSignature(GLuaItem item, string tag, WikiDoc definition) {
		switch (tag) {
			case "GLOBAL":
				SignatureGlobals[definition.Search!] = definition;
				break;
			case "FUNCTION":
				SignatureFunctions[definition.Search!] = definition;
				break;
			case "HOOK":
				AddMeta(item.Label, definition);
				AddMeta(item.InsertText ?? item.Label, definition);
				break;
			case "META_FUNCTION":
				AddMeta(item.InsertText ?? item.Label, definition);
				break;
		}
	}

	private static void Add(GLuaList list, GLuaItem? item) {
		if (item is not null) list.Items.Add(item);
	}

	private void AddWiki(WikiData wiki) {
		bool gamemodeAdded = false;

		foreach ((string key, Dictionary<string, WikiDoc> entries) in wiki.Sections) {
			switch (key) {
				case "HOOKS":
					foreach ((string family, WikiDoc familyDefinition) in entries) {
						GLuaList familyList = Hook.Child(family);
						bool addToMeta = family is not ("GM" or "GAMEMODE");

						foreach ((string name, WikiDoc definition) in familyDefinition.Members ?? []) {
							GLuaItem? item = Create("HOOK", name, "Event", definition, family + ":" + name);

							if (item is null) continue;

							if (addToMeta) MetaFunc.Items.Add(item);

							familyList.Items.Add(item);
							Hook.Items.Add(item);
						}

						Add(FunctionDecl, Create("FUNC_DECL_HOOK", "function " + family + ":", "Constructor", familyDefinition, family + ":", "function " + family));

						if (family == "GM" && !gamemodeAdded) {
							gamemodeAdded = true;
							familyDefinition.Search = familyDefinition.Search == "GAMEMODE" ? "GM" : "GAMEMODE";
							Add(FunctionDecl, Create("FUNC_DECL_HOOK", "function GAMEMODE:", "Constructor", familyDefinition, "GAMEMODE:", "function GAMEMODE"));
						}
					}

					break;
				case "LIBRARIES":
					AddLibraries(entries, null, "", isPackage: true);
					break;
				case "CLASSES":
					foreach ((string className, WikiDoc data) in entries) {
						foreach ((string name, WikiDoc definition) in data.Members ?? []) {
							definition.Flags |= WikiFlags.Method;
							Add(MetaFunc, Create("META_FUNCTION", name, "Method", definition, className + ":" + name));
						}
					}

					break;
				case "PANELS":
					foreach ((string panelName, WikiDoc definition) in entries) {
						GLuaItem? item = Create("PANEL", panelName, "Constant", definition);
						Add(Panel, item);
						Add(Global, item);

						if (definition.Members is not { } members) continue;

						if (!LibraryFunc.TryGetValue(panelName, out GLuaList? panelList) || panelList is null) LibraryFunc[panelName] = panelList = new GLuaList();

						foreach ((string function, WikiDoc functionDefinition) in members) {
							Add(panelList, Create("PANEL_FUNCTION", function, "Method", functionDefinition));
							Add(MetaFunc, Create("META_FUNCTION", function, "Method", functionDefinition, panelName + ":" + function));
						}
					}

					break;
				case "STRUCTS":
					foreach ((string structName, WikiDoc definition) in entries) {
						GLuaItem? item = Create("STRUCT", structName, "Struct", definition);
						Add(Global, item);

						bool containsFunction = false;
						GLuaList fields = Struct[structName] = new GLuaList(true);

						foreach ((string field, WikiDoc fieldDefinition) in definition.Members ?? []) {
							bool isFunction = fieldDefinition.Type == "function";
							Add(fields, Create("STRUCT_FIELD", field, isFunction ? "Event" : "Struct", fieldDefinition, structName + "." + field, isFunction ? field : field + " = "));
							containsFunction |= isFunction;
						}

						if (containsFunction && structName.ToUpperInvariant() == structName) {
							Add(FunctionDecl, Create("FUNC_DECL_STRUCT", "function " + structName + ":", "Struct", definition, structName + ":", "function " + structName));
							Add(Enum, item);
						}
					}

					break;
				case "GLOBALS":
					foreach ((string name, WikiDoc definition) in entries) Add(Global, Create("GLOBAL", name, "Function", definition));

					break;
				case "ENUMS":
					foreach ((string name, WikiDoc definition) in entries) {
						string family = definition.Family ?? string.Empty;

						if (!EnumFamily.TryGetValue(family, out GLuaList? familyList)) EnumFamily[family] = familyList = new GLuaList();

						if (!EnumFamilySub.TryGetValue(family, out GLuaList? subList)) EnumFamilySub[family] = subList = new GLuaList();

						GLuaItem? item = Create("ENUM", name, "Enum", definition, name, definition.Has(WikiFlags.ReferenceOnly) ? definition.Value : null);

						if (item is null) continue;

						Enum.Items.Add(item);
						familyList.Items.Add(item);

						Match match = EnumCompletions().Match(name);

						if (match.Success && match.Groups[4].Success) subList.Items.Add(item.With(item.Label, name[(match.Groups[3].Length + 1)..]));
					}

					break;
			}
		}

		foreach ((string structName, GLuaList completions) in Struct) {
			if (Hook.Find(structName) is { } hooks) completions.Items.AddRange(hooks.Items);
		}

		Generic.Items.AddRange(Global.Items);
		Generic.Items.AddRange(Enum.Items);
		GenericFunc.Items.AddRange(Global.Items);
		GenericFunc.Items.AddRange(MetaFunc.Items);
	}

	private void AddLibraries(Dictionary<string, WikiDoc> entries, GLuaList? completions, string prefix, bool isPackage) {
		foreach ((string library, WikiDoc functions) in entries) {
			if (functions.Members is { } members) {
				GLuaItem? item = Create("PACKAGE", prefix + library, "Module", functions, null, library);

				if (item is not null && !isPackage && functions.Description.IsEmpty) item.NoDoc = true;

				Add(completions ?? Global, item);

				GLuaList children = new();
				LibraryFunc[prefix + library] = children;
				AddLibraries(members, children, prefix + library + ".", isPackage: false);
				continue;
			}

			LibraryFunc[prefix + library] = null;

			GLuaItem? function = Create("FUNCTION", prefix + library, functions.Has(WikiFlags.Function) ? "Function" : "Constant", functions, null, library);

			if (function is not null && !isPackage && functions.Description.IsEmpty) function.NoDoc = true;

			if (completions is not null) Add(completions, function);
		}
	}

	private void LoadSounds(string path) {
		if (!File.Exists(path)) return;

		using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
		Dictionary<string, string> order = new() { ["garrysmod"] = "1", ["hl2"] = "2", ["css"] = "3", ["tf2"] = "4" };

		foreach (JsonProperty game in document.RootElement.EnumerateObject()) {
			GLuaList gameList = SoundsByGame[game.Name] = new GLuaList();
			Step(game.Name, game.Value, gameList, order);
		}
	}

	private void Step(string game, JsonElement tree, GLuaList gameRoot, Dictionary<string, string> order) {
		if (tree.GetProperty("children") is { ValueKind: JsonValueKind.Object } children) {
			foreach (JsonProperty child in children.EnumerateObject()) Step(game, child.Value, gameRoot, order);
		}

		string treePath = tree.GetProperty("path").GetString() ?? string.Empty;
		string[] folders = treePath.TrimEnd('/').Split('/');
		string? rank = order.GetValueOrDefault(game);

		foreach (JsonElement fileElement in tree.GetProperty("files").EnumerateArray()) {
			string file = fileElement.GetString() ?? string.Empty;
			GLuaItem item = new(game + "! " + file, "File") { Detail = "(" + game + ")", NoDoc = true, InsertText = file, SortText = rank is null ? "25" : "2" + rank };
			GLuaList all = SoundsAll;
			GLuaList byGame = gameRoot;

			for (int j = 0; j < folders.Length; j++) {
				string folder = folders[j] + "/";

				if (!all.Has(folder) || !byGame.Has(folder)) {
					GLuaItem folderItem = new(game + "! " + folder, "Folder") { Detail = item.Detail, NoDoc = true, InsertText = folders[j], SortText = rank is null ? "15" : "1" + rank };

					if (!all.Has(folder)) {
						all.Items.Add(folderItem);
						all.Child(folder);
					}

					if (!byGame.Has(folder)) {
						byGame.Items.Add(folderItem.With(folder, folderItem.InsertText));
						byGame.Child(folder);
					}
				}

				all = all.Find(folder)!;
				byGame = byGame.Find(folder)!;

				if (j == folders.Length - 1) {
					all.Items.Add(item);
					byGame.Items.Add(item.With(file, item.InsertText));
				}
			}
		}
	}
}
