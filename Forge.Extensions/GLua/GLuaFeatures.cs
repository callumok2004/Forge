using Forge.Common.Commands;
using Forge.Extensions.GLua.Completion;
using Forge.Extensions.GLua.Navigation;
using Forge.Extensions.GLua.Signatures;
using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua;

public sealed class GLuaFeatures(GLuaExtension extension, GLuaCatalog catalog, GLuaCompletions completions, GLuaSignatures signatures, GLuaNavigation navigation, ICommandHost host) : ILanguageFeatures
{
	private static readonly LanguageTriggers Triggers = new(GLuaCompletions.TriggerCharacters, "(,", ")", string.Empty, string.Empty);

	private GLuaItem[] _items = [];

	public bool Handles(string path) => path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);

	public LanguageTriggers GetTriggers() => Triggers;

	private string Text(string path) => host.GetDocumentText(path) ?? (File.Exists(path) ? File.ReadAllText(path) : string.Empty);

	public Task<CompletionList> CompletionsAsync(string path, int line, int column, char trigger, CompletionTrigger kind) {
		string text = Text(path);
		string lineText = GLuaNavigation.LineText(text, line - 1);
		string term = lineText[..Math.Clamp(column - 1, 0, lineText.Length)];
		bool isTrigger = kind == CompletionTrigger.Character && trigger != '\0';

		completions.Reset();
		IReadOnlyList<GLuaItem> items = completions.Provide(new CompletionContext(path, text, line - 1, term.Length, term, trigger, isTrigger));
		GLuaItem[] snapshot = [.. items];
		CompletionEntry[] entries = new CompletionEntry[snapshot.Length];

		for (int i = 0; i < snapshot.Length; i++) {
			GLuaItem item = snapshot[i];
			entries[i] = new CompletionEntry(i, item.Label, string.Empty, string.Empty, item.InsertText ?? item.Label, item.Detail ?? string.Empty, item.Kind, item.FilterText ?? item.Label, false, line, item.ReplaceStartColumn, item.ReplaceStartColumn > 0 ? column : 0) {
				SortText = item.SortText ?? item.Label,
				Deprecated = item.Deprecated,
			};
		}

		_items = snapshot;
		return Task.FromResult(new CompletionList(entries, completions.AnyIncomplete));
	}

	public Task<CompletionDetail?> ResolveCompletionAsync(int index) {
		GLuaItem[] items = _items;

		if ((uint)index >= (uint)items.Length) return Task.FromResult<CompletionDetail?>(null);

		GLuaItem item = items[index];

		if (item.Documentation is { } documentation) return Task.FromResult<CompletionDetail?>(new CompletionDetail(item.Detail ?? string.Empty, documentation, []));

		if (item.NoDoc && item.Doc is null) return Task.FromResult<CompletionDetail?>(new CompletionDetail(item.Detail ?? string.Empty, string.Empty, []));

		WikiDoc? doc = item.Doc ?? (catalog.Docs.TryGetValue(item.DocTag ?? item.Label, out WikiDoc? found) ? found : item.DocTag is { } tag ? completions.Image(tag) : null);

		if (doc is null) return Task.FromResult<CompletionDetail?>(new CompletionDetail(item.Detail ?? string.Empty, string.Empty, []));

		if (doc.RawImage is { } image) return Task.FromResult<CompletionDetail?>(new CompletionDetail(item.Detail ?? string.Empty, "![" + WikiMarkdown.Escape(item.Label) + "](" + new Uri(image).AbsoluteUri + ")", []));

		string detail = doc.Tag == "ENUM" ? doc.Value ?? string.Empty : doc.NetworkVar is { } networkVar ? "(" + networkVar + ")" : item.Detail ?? string.Empty;

		return Task.FromResult<CompletionDetail?>(new CompletionDetail(detail, WikiMarkdown.Resolve(doc, item.Label), []));
	}

	public Task<SignatureHelp?> SignatureHelpAsync(string path, int line, int column, char trigger) {
		string lineText = GLuaNavigation.LineText(Text(path), line - 1);

		if (signatures.Provide(lineText[..Math.Clamp(column - 1, 0, lineText.Length)]) is not { Signatures.Count: > 0 } help) return Task.FromResult<SignatureHelp?>(null);

		List<SignatureHelp> overloads = [.. help.Signatures.Select(Convert)];
		int active = Math.Clamp(help.ActiveSignature, 0, overloads.Count - 1);

		return Task.FromResult<SignatureHelp?>(overloads[active] with { OverloadIndex = active, OverloadCount = overloads.Count, Overloads = overloads });
	}

	private static SignatureHelp Convert(GLuaSignature signature) {
		List<(int Start, int Length)> parameters = [];
		int search = 0;

		foreach (GLuaParameter parameter in signature.Parameters) {
			int at = signature.Label.IndexOf(parameter.Label, search, StringComparison.Ordinal);

			if (at < 0) at = signature.Label.IndexOf(parameter.Label, StringComparison.Ordinal);

			parameters.Add(at < 0 ? (0, 0) : (at, parameter.Label.Length));

			if (at >= 0) search = at + parameter.Label.Length;
		}

		string? parameterDocumentation = signature.ActiveParameter < signature.Parameters.Count ? signature.Parameters[signature.ActiveParameter].Documentation : null;
		string documentation = string.Join("\n\n", new[] { parameterDocumentation, signature.Documentation }.Where(static part => part is { Length: > 0 }));

		return new SignatureHelp(signature.Label, parameters, documentation, signature.ActiveParameter, 0, 1);
	}

	public Task<string?> HoverAsync(string path, int line, int column) {
		string? hover = navigation.Hover(path, Text(path), line - 1, column - 1);

		if (extension.HeatmapHover(path, line - 1) is { } heat) hover = hover is null ? heat : heat + Forge.Language.HoverMarkdown.RowSeparator + hover;

		return Task.FromResult(hover);
	}

	public IReadOnlyDictionary<int, Forge.Theming.Color>? LineBackgrounds(string path) => extension.Heatmap(path);

	public async Task<DefinitionResult?> DefinitionAsync(string path, int line, int column) {
		IReadOnlyList<HostLocation> locations = await navigation.DefinitionAsync(path, Text(path), line - 1, column - 1).ConfigureAwait(false);
		return locations.Count == 0 ? null : new DefinitionResult(0, 0, 0, locations);
	}

	public Task<IReadOnlyList<HostLocation>> ReferencesAsync(string path, int line, int column) => Task.FromResult(navigation.References(path, Text(path), line - 1, column - 1));
}
