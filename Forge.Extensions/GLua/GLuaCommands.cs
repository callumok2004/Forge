using System.Text;
using System.Text.RegularExpressions;

using Forge.Common.Commands;
using Forge.Extensions.GLua.Index;
using Forge.Extensions.GLua.Parsing;
using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua;

public static partial class GLuaCommands
{
	private const string Category = "GLua Enhanced";

	private const string LocalizePrompt = "How would you like to localize global calls?\n\nGreedy: local net_Start = net.Start\nLazy: local net = net\n\nLua functions cannot have more than 60 upvalues (locals) in their scope. \"Greedy\" may cause this error in particularly large files.";

	public static void Register(ICommandRegistry commands, GLuaExtension extension) {
		commands.Register(new Command("glua-enhanced.findGlobals", "Find Globals", Category, host => Run(extension, host, () => FindGlobals(extension, host))));
		commands.Register(new Command("glua-enhanced.bytecodeHeatmap", "Generate Bytecode Heatmap", Category, host => Run(extension, host, () => BytecodeHeatmap.Generate(extension, host))));
		commands.Register(new Command("glua-enhanced.optimizeGlobals", "Localize Global Calls", Category, host => Run(extension, host, () => OptimizeActive(extension, host))));
		commands.Register(new Command("glua-enhanced.optimizeGlobalsWorkspace", "Localize Global Calls (Workspace)", Category, host => Run(extension, host, () => OptimizeWorkspace(extension, host))));
	}

	private static void Run(GLuaExtension extension, ICommandHost host, Action action) {
		_ = extension.Ready.ContinueWith(ready => {
			if (ready.IsCompletedSuccessfully) action();
		}, TaskScheduler.Default);
	}

	private static void FindGlobals(GLuaExtension extension, ICommandHost host) {
		FileTokenData compiled = extension.Index!.Compiled;
		List<string> names = [.. compiled.G.Keys.Order(StringComparer.Ordinal)];

		if (names.Count == 0) {
			host.ShowMessage(MessageSeverity.Info, "No globals found!", []);
			return;
		}

		string? workspace = host.GetWorkspacePath();
		StringBuilder markdown = new("# Defined Globals\n\n| Name | Definition(s) |\n|-|-|");

		foreach (string name in names) {
			List<LuaNode> tokens = compiled.G[name];

			if (tokens.Count == 0) continue;

			StringBuilder definitions = new();

			foreach (LuaNode token in tokens) {
				string path = token.Path ?? string.Empty;
				string relative = workspace is not null ? Path.GetRelativePath(workspace, path).Replace('\\', '/') : path;
				string link = new Uri(path).AbsoluteUri + "#L" + token.StartLine + "," + (token.StartColumn + 1);
				definitions.Append('[').Append(WikiMarkdown.Escape(relative)).Append("](").Append(link).Append(") (Line ").Append(token.StartLine);

				if (token.StartColumn > 0) definitions.Append(':').Append(token.StartColumn);

				definitions.Append(")<br>");
			}

			string joined = definitions.ToString();

			if (joined.EndsWith("<br>", StringComparison.Ordinal)) joined = joined[..^4];

			markdown.Append("\n| ").Append(WikiMarkdown.Escape(name)).Append(" | ").Append(joined).Append(" |");
		}

		string file = Path.Combine(GLuaExtension.TempRoot, "glua_enhanced_globals.md");
		Directory.CreateDirectory(GLuaExtension.TempRoot);
		File.WriteAllText(file, markdown.ToString());
		host.OpenFile(file);
	}

	private static void OptimizeActive(GLuaExtension extension, ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path) {
			host.ShowMessage(MessageSeverity.Error, "No active text editor!", []);
			return;
		}

		host.ShowMessage(MessageSeverity.Info, LocalizePrompt, ["Greedy", "Lazy"], choice => {
			if (choice < 0) return;

			string text = host.GetDocumentText(path) ?? File.ReadAllText(path);

			if (extension.Index!.Get(path, text) is not { } parsed) return;

			_ = host.ApplyWorkspaceEditAsync(new WorkspaceEdit([new WorkspaceOperation(WorkspaceOperationKind.Edit, path, GlobalsOptimizer.Optimize(choice == 0, parsed, path))]));
		});
	}

	private static void OptimizeWorkspace(GLuaExtension extension, ICommandHost host) {
		host.ShowMessage(MessageSeverity.Info, LocalizePrompt, ["Greedy", "Lazy"], choice => {
			if (choice < 0) return;

			host.ShowMessage(MessageSeverity.Info, "What files would you like to optimize the global calls of?", ["Entire Workspace", "Open Files", "Closed Files"], filter => {
				if (filter < 0) return;

				GLuaIndex index = extension.Index!;
				HashSet<string> open = [with(StringComparer.OrdinalIgnoreCase), .. index.OpenPaths()];
				List<string> files;

				if (filter == 1) {
					if (open.Count == 0) {
						host.ShowMessage(MessageSeverity.Error, "No open Lua files!", []);
						return;
					}

					files = [.. open];
				}
				else {
					string? workspace = host.GetWorkspacePath();
					files = workspace is null ? [] : [.. Directory.EnumerateFiles(workspace, "*.lua", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })];

					if (files.Count == 0) {
						host.ShowMessage(MessageSeverity.Error, "No Lua files found in this workspace!", []);
						return;
					}

					if (filter == 2) files.RemoveAll(open.Contains);
				}

				_ = Task.Run(async () => {
					const string StatusId = "glua.optimize";
					List<WorkspaceOperation> operations = [];

					for (int i = 0; i < files.Count; i++) {
						host.SetStatusItem(StatusId, "sync~spin", "Optimizing globals: Parsing files... " + (i * 100 / files.Count) + "%", null);
						string path = files[i];
						string? text = host.GetDocumentText(path) ?? (File.Exists(path) ? File.ReadAllText(path) : null);

						if (text is null || index.Get(path, text) is not { } parsed) continue;

						IReadOnlyList<HostTextEdit> edits = GlobalsOptimizer.Optimize(choice == 0, parsed, path);

						if (edits.Count > 0) operations.Add(new WorkspaceOperation(WorkspaceOperationKind.Edit, path, edits));
					}

					host.SetStatusItem(StatusId, "sync~spin", "Optimizing globals: Applying optimizations...", null);
					await host.ApplyWorkspaceEditAsync(new WorkspaceEdit(operations)).ConfigureAwait(false);
					host.RemoveStatusItem(StatusId);
				});
			});
		});
	}
}

public static partial class GlobalsOptimizer
{
	private enum Realm
	{
		Shared,
		Client,
		Server,
	}

	[GeneratedRegex(@"([^\\\/]+?)\.lua$")]
	private static partial Regex FileName();

	[GeneratedRegex(@"^(?:(?:sv|sh|cl)_|.+$)")]
	private static partial Regex RealmPrefix();

	private static Realm RealmOf(List<WikiDoc> docs) {
		if (docs.Count == 1) {
			WikiDoc doc = docs[0];

			if (doc.Has(WikiFlags.HasServer) && doc.Has(WikiFlags.HasClient)) return Realm.Shared;

			return doc.Has(WikiFlags.HasServer) ? Realm.Server : doc.Has(WikiFlags.HasClient) ? Realm.Client : Realm.Shared;
		}

		bool server = false;
		bool client = false;

		foreach (WikiDoc doc in docs) {
			if (doc.Has(WikiFlags.HasServer) && doc.Has(WikiFlags.HasClient)) return Realm.Shared;

			if (doc.Has(WikiFlags.HasServer)) server = true;
			else if (doc.Has(WikiFlags.HasClient)) client = true;

			if (server && client) return Realm.Shared;
		}

		return server ? Realm.Server : client ? Realm.Client : Realm.Shared;
	}

	private static bool Skips(LuaNode token) => token.IsLocal || (token.HasBase && token.Base is { Type: LuaNodeType.MemberExpression, Indexer: ':' });

	public static IReadOnlyList<HostTextEdit> Optimize(bool greedy, ParsedFile tokens, string path) {
		bool insertWhitespace = tokens.List.Count > 1 && tokens.Lines.Min is { } min && tokens.List[1].StartLine - 1 == min;
		Dictionary<string, string> preoptimized = [with(StringComparer.Ordinal)];
		Dictionary<string, List<LuaNode>> optimize = [with(StringComparer.Ordinal)];
		Dictionary<string, Realm> realms = [with(StringComparer.Ordinal)];

		foreach (LuaNode local in tokens.Of(LuaNodeType.LocalStatement)?.List ?? []) {
			for (int j = 0; j < local.Variables!.Count; j++) {
				if (j >= local.Init!.Count || !local.Variables[j].IsLocal || local.Init[j].Type is not (LuaNodeType.Identifier or LuaNodeType.MemberExpression)) continue;

				if (local.Parent != tokens.List[0]) continue;

				if (TokenAnalyzer.FullFunctionCall(local.Init[j]) is { } call) preoptimized[string.Concat(call)] = local.Variables[j].Name!;

				if (insertWhitespace && tokens.List[1] != local) insertWhitespace = false;
			}
		}

		foreach (LuaNode call in tokens.Of(LuaNodeType.CallExpression)?.List ?? []) {
			if (call.Base is null) continue;

			List<WikiDoc>? docs = null;
			List<string>? full = TokenAnalyzer.FullFunctionCall(call, token => {
				if (Skips(token)) return false;

				if (greedy && docs is null && token.Docs is { } found) docs = found;

				return true;
			});

			if (full is null) continue;

			string global = string.Concat(full);

			if (!greedy || docs is null) {
				if (!optimize.TryGetValue(full[0], out List<LuaNode>? lazy)) optimize[full[0]] = lazy = [];

				lazy.Add(call.Base);
				continue;
			}

			if (full.Count == 1 && full[0] == "DEFINE_BASECLASS") continue;

			if (!optimize.TryGetValue(global, out List<LuaNode>? ranges)) optimize[global] = ranges = [];

			if (full.Count > 1) realms.TryAdd(global, RealmOf(docs));

			ranges.Add(call.Base);
		}

		if (optimize.Count == 0) return [];

		string fileName = FileName().Match(path) is { Success: true } named ? named.Groups[1].Value : string.Empty;
		string prefix = RealmPrefix().Match(fileName).Value;
		bool needsRealm = prefix is not ("sv_" or "cl_" or "init");
		StringBuilder header = new();
		List<HostTextEdit> edits = [];

		foreach ((string name, List<LuaNode> ranges) in optimize) {
			bool already = preoptimized.TryGetValue(name, out string? existing);
			string replacement = existing ?? name.Replace('.', '_');

			if (!already) {
				string realm = needsRealm && realms.TryGetValue(name, out Realm found) && found != Realm.Shared ? found == Realm.Server ? "SERVER and " : "CLIENT and " : "";
				header.Append("local ").Append(replacement).Append(" = ").Append(realm).Append(name).Append('\n');
			}

			if (replacement == name) continue;

			foreach (LuaNode range in ranges) edits.Add(new HostTextEdit(range.StartLine, range.StartColumn + 1, range.EndLine, range.EndColumn + 1, replacement));
		}

		if (header.Length > 0 && insertWhitespace) header.Append('\n');

		if (header.Length > 0) edits.Insert(0, new HostTextEdit(1, 1, 1, 1, header.ToString()));

		return edits;
	}
}
