using Forge.Extensions.GLua;
using Forge.Extensions.GLua.Completion;
using Forge.Extensions.GLua.Index;
using Forge.Extensions.GLua.Navigation;
using Forge.Extensions.GLua.Parsing;
using Forge.Extensions.GLua.Signatures;
using Forge.Extensions.GLua.Wiki;

namespace Forge.Tests;

public sealed class GLuaTests
{
	private static readonly Lazy<GLuaCatalog> Catalog = new(() => GLuaCatalog.Build(WikiReader.Load(Path.Combine(GLuaExtension.Directory, "resources", "wiki.json")), Path.Combine(GLuaExtension.Directory, "resources", "sounds.json")));

	private static (GLuaIndex Index, GLuaCompletions Completions, GLuaSignatures Signatures) Services() {
		GLuaIndex index = new(Catalog.Value, () => null, null);
		GLuaSignatures signatures = new(Catalog.Value, index);
		return (index, new GLuaCompletions(Catalog.Value, index, signatures, () => null), signatures);
	}

	private static List<string> Labels(GLuaCompletions completions, string path, string text, char trigger = '\0') {
		string term = text.Split('\n')[^1];
		int line = text.Count(static c => c == '\n');
		return [.. completions.Provide(new CompletionContext(path, text, line, term.Length, term, trigger, trigger != '\0')).Select(static item => item.Label)];
	}

	[Fact]
	public void ParsesGLuaSyntaxExtensions() {
		LuaNode chunk = LuaParser.Parse("if a != b && !c || d then continue end // note\n/* block */ local x = 1");
		Assert.Equal(2, chunk.Body!.Count);
		Assert.Equal(LuaNodeType.IfStatement, chunk.Body[0].Type);
		Assert.Equal(LuaNodeType.LocalStatement, chunk.Body[1].Type);
		Assert.Equal(2, chunk.Body[1].StartLine);
	}

	[Fact]
	public void RejectsInvalidSyntax() => Assert.Throws<LuaSyntaxException>(() => LuaParser.Parse("local = ="));

	[Fact]
	public void LocationsMatchLuaparse() {
		LuaNode chunk = LuaParser.Parse("local a = net.Start(\"x\")");
		LuaNode call = chunk.Body![0].Init![0];
		Assert.Equal(LuaNodeType.CallExpression, call.Type);
		Assert.Equal((1, 10, 1, 24), (call.StartLine, call.StartColumn, call.EndLine, call.EndColumn));
	}

	[Fact]
	public void WikiDocsResolveLazily() {
		Assert.True(Catalog.Value.Docs.TryGetValue("FUNCTION:net.Start", out WikiDoc? doc));
		Assert.StartsWith("Begins a new net message", doc!.Description.Value, StringComparison.Ordinal);
		Assert.Contains("[$(notebook) Wiki](https://wiki.facepunch.com/gmod/net.Start)", WikiMarkdown.Resolve(doc).Replace("\\", ""), StringComparison.Ordinal);
	}

	[Fact]
	public void CompletesLibraryHookEnumAndPanel() {
		(GLuaIndex index, GLuaCompletions completions, _) = Services();
		using GLuaIndex disposable = index;

		Assert.Contains("net.Start", Labels(completions, "a.lua", "net.", '.'));
		Assert.Contains("GM:PlayerSay", Labels(completions, "a.lua", "hook.Add(\"", '"'));
		Assert.Contains("DButton", Labels(completions, "a.lua", "vgui.Create(\"", '"'));
		Assert.Contains("ACT_IDLE", Labels(completions, "a.lua", "x = ACT_"));
	}

	[Fact]
	public void CompletesWorkspaceGlobalsScopedLocalsAndNetworkStrings() {
		(GLuaIndex index, GLuaCompletions completions, _) = Services();
		using GLuaIndex disposable = index;

		index.Parse("b.lua", "MyAddon = {}\nfunction MyAddon.Helper() end\nutil.AddNetworkString(\"my_message\")", keepOnError: false);

		const string Text = "local function f(value)\n\tlocal inner = 1\n\tprint(M)\nend";
		index.Parse("c.lua", Text, keepOnError: false);
		List<string> labels = [.. completions.Provide(new CompletionContext("c.lua", Text, 2, 8, "\tprint(M", '\0', false)).Select(static item => item.Label)];
		Assert.Contains("MyAddon", labels);
		Assert.Contains("value", labels);
		Assert.Contains("inner", labels);
		Assert.Contains("MyAddon.Helper", Labels(completions, "c.lua", "MyAddon.", '.'));
		Assert.Contains("my_message", Labels(completions, "c.lua", "net.Start(\"", '"'));
	}

	[Fact]
	public void SignaturesCoverLibrariesAndHookCallbacks() {
		(GLuaIndex index, _, GLuaSignatures signatures) = Services();
		using GLuaIndex disposable = index;

		GLuaSignatureHelp? help = signatures.Provide("net.Start(\"x\", ");
		Assert.NotNull(help);
		Assert.Equal(1, help!.Signatures[0].ActiveParameter);

		GLuaSignatureHelp? hook = signatures.Provide("hook.Add(\"PlayerSay\", \"id\", function(ply, ");
		Assert.NotNull(hook);
		Assert.StartsWith("sender: Player, text: string", hook!.Signatures[0].Label, StringComparison.Ordinal);
		Assert.Equal(1, hook.Signatures[0].ActiveParameter);
	}

	[Fact]
	public void HoverShowsWikiAndStringLength() {
		(GLuaIndex index, _, _) = Services();
		using GLuaIndex disposable = index;
		GLuaNavigation navigation = new(Catalog.Value, index, Path.GetTempPath(), null!);
		const string Text = "net.Start(\"abc\")";

		Assert.Contains("net.Start(messageName: string, unreliable: boolean)", navigation.Hover("h.lua", Text, 0, 5), StringComparison.Ordinal);
		Assert.Contains("Length: 3 bytes", navigation.Hover("h.lua", Text, 0, 12), StringComparison.Ordinal);
	}

	[Fact]
	public void GreedyOptimizerLocalizesDocumentedCalls() {
		(GLuaIndex index, _, _) = Services();
		using GLuaIndex disposable = index;
		ParsedFile parsed = index.Parse("sh_test.lua", "net.Start(\"x\")\nnet.Start(\"y\")", keepOnError: false)!;

		IReadOnlyList<Forge.Common.Commands.HostTextEdit> edits = GlobalsOptimizer.Optimize(greedy: true, parsed, "sh_test.lua");

		Assert.Equal("local net_Start = net.Start\n\n", edits[0].NewText);
		Assert.Equal(3, edits.Count);
		Assert.All(edits.Skip(1), static edit => Assert.Equal("net_Start", edit.NewText));
	}

	[Fact]
	public void HeatmapReadsLuaSharedBytecode() {
		byte[] bytecode = LuaShared.Compile("local t = {}\nfor i = 1, 10 do\n\tt[i] = i .. \"x\"\nend\nprint(t)", "heat.lua");
		(Dictionary<int, BytecodeHeatmap.LineHeat> heat, double max) = BytecodeHeatmap.Compute(bytecode);

		Assert.True(max > 0);
		Assert.True(heat[3].Opcodes.ContainsKey("CAT"));
		Assert.True(heat[5].Opcodes.ContainsKey("GGET"));
	}

	[Fact]
	public void ManifestGrammarClaimsLuaFiles() {
		Forge.Extensions.Host.BuiltInContributions.RegisterGrammars();

		Forge.Syntax.GrammarEntry? entry = Forge.Syntax.GrammarIndex.ForPath("addon/lua/autorun/init.lua");
		Assert.Equal("glua", entry?.Id);
		Assert.Equal("source.glua", entry?.ScopeName);
		Assert.Equal("--", entry?.LineComment);
		Assert.NotNull(Forge.Syntax.TextMate.Grammars.ForId("glua"));
	}
}
