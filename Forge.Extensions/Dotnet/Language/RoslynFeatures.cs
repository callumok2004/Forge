using Forge.Common.Commands;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Server.Features;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Extensions.Dotnet.Language;

public sealed class RoslynFeatures(RoslynLanguageClient server, DotnetSettings settings, CodeActionRunner actions, ICommandHost host) : ILanguageFeatures
{
	public bool Handles(string path) => ProjectDiscovery.IsSource(path);

	public async Task<CompletionList> CompletionsAsync(string path, int line, int column, char trigger, CompletionTrigger kind) {
		ServerCompletionList list = await server.Completion.RequestAsync(path, line, column, trigger, (int)kind).ConfigureAwait(false);
		IReadOnlyList<ServerCompletion> items = list.Items;
		CompletionEntry[] entries = new CompletionEntry[items.Count];

		for (int i = 0; i < items.Count; i++) {
			ServerCompletion item = items[i];
			entries[i] = new CompletionEntry(item.Index, item.Label, item.LabelDetail, item.LabelDescription, item.InsertText, item.Detail, item.Kind, item.FilterText, item.Preselect, item.ReplaceLine, item.ReplaceStartColumn, item.ReplaceEndColumn);
		}

		return new CompletionList(entries, list.IsIncomplete);
	}

	public async Task<CompletionDetail?> ResolveCompletionAsync(int index) {
		if (await server.Completion.ResolveAsync(index).ConfigureAwait(false) is not { } detail) return null;

		HostTextEdit[] edits = new HostTextEdit[detail.AdditionalEdits.Count];

		for (int i = 0; i < edits.Length; i++) {
			ServerTextEdit edit = detail.AdditionalEdits[i];
			edits[i] = new HostTextEdit(edit.StartLine, edit.StartColumn, edit.EndLine, edit.EndColumn, edit.NewText);
		}

		return new CompletionDetail(detail.Detail, detail.Documentation, edits);
	}

	public async Task<SignatureHelp?> SignatureHelpAsync(string path, int line, int column, char trigger) {
		if (await server.Signatures.RequestAsync(path, line, column, trigger).ConfigureAwait(false) is not { } help) return null;

		ServerSignature active = help.Active;

		return new SignatureHelp(active.Label, active.Parameters, active.Documentation, help.ParameterIndex, help.ActiveIndex, help.Overloads.Count);
	}

	public Task<string?> HoverAsync(string path, int line, int column) => server.Hover.RequestAsync(path, line, column);

	public async Task<IReadOnlyList<DocumentHighlight>> DocumentHighlightsAsync(string path, int line, int column) {
		IReadOnlyList<ServerHighlight> highlights = await server.Highlights.RequestAsync(path, line, column).ConfigureAwait(false);

		return [.. highlights.Select(static highlight => new DocumentHighlight(highlight.Line, highlight.StartColumn, highlight.EndLine, highlight.EndColumn, (DocumentHighlightKind)Math.Clamp(highlight.Kind, 1, 3)))];
	}

	public async Task<DefinitionResult?> DefinitionAsync(string path, int line, int column) {
		Task<IReadOnlyList<ServerLocation>> pendingImplementations = settings.PreferImplementation ? server.Navigation.ImplementationsAsync(path, line, column) : Task.FromResult<IReadOnlyList<ServerLocation>>([]);
		IReadOnlyList<ServerLocation> definitions = await server.Navigation.DefinitionsAsync(path, line, column).ConfigureAwait(false);
		IReadOnlyList<ServerLocation> implementations = await pendingImplementations.ConfigureAwait(false);
		IReadOnlyList<ServerLocation> chosen = implementations.Count == 1 && !definitions.Any(definition => string.Equals(definition.Path, implementations[0].Path, StringComparison.OrdinalIgnoreCase) && definition.Line == implementations[0].Line && definition.Column == implementations[0].Column) ? implementations : definitions;

		return chosen.Count == 0 ? null : new DefinitionResult(0, 0, 0, Locations(chosen));
	}

	public async Task<IReadOnlyList<HostLocation>> ImplementationsAsync(string path, int line, int column) => Locations(await server.Navigation.ImplementationsAsync(path, line, column).ConfigureAwait(false));

	public async Task<IReadOnlyList<HostLocation>> ReferencesAsync(string path, int line, int column) => Locations(await server.Navigation.ReferencesAsync(path, line, column).ConfigureAwait(false));

	private static HostLocation[] Locations(IReadOnlyList<ServerLocation> found) => [.. found.Select(static location => new HostLocation(location.Path, location.Line, location.Column, location.EndLine, location.EndColumn, location.Preview))];

	public async Task<IReadOnlyList<CodeActionEntry>> CodeActionsAsync(string path, int startLine, int startColumn, int endLine, int endColumn, bool automatic) {
		IReadOnlyList<ServerCodeAction> found = await server.CodeActions.RequestAsync(path, startLine, startColumn, endLine, endColumn, automatic).ConfigureAwait(false);

		return [.. found.Select(static action => new CodeActionEntry(action.Index, action.Title, action.Kind, action.IsPreferred, action.DisabledReason))];
	}

	public Task ExecuteCodeActionAsync(int index) => actions.ExecuteAsync(host, index);

	public EvaluatableExpression? EvaluatableExpressionAt(string path, int line, string lineText, int column) => Debugger.DebugExpressions.Evaluatable(lineText, line, column, server.SemanticTokens.Cached(path));

	public bool ProvidesInlineValues => true;

	public void InlineValues(string path, int firstLine, int lastLine, Func<int, string> lineText, List<InlineValueLookup> into) => Debugger.DebugExpressions.InlineValues(firstLine, lastLine, lineText, into);

	public int CodeLensVersion => server.CodeLenses.Version;

	public async Task<IReadOnlyList<CodeLensEntry>> CodeLensesAsync(string path) {
		IReadOnlyList<ServerCodeLens> lenses = await server.CodeLenses.RequestAsync(path).ConfigureAwait(false);

		return [.. lenses.Select(static lens => new CodeLensEntry(lens.Index, lens.Line, lens.Column, lens.Title))];
	}

	public async Task<CodeLensTitle?> ResolveCodeLensAsync(string path, int index) => await server.CodeLenses.ResolveAsync(path, index).ConfigureAwait(false) is { } command ? new CodeLensTitle(command.Title, command.Command is { Length: > 0 }) : null;

	public async Task<CodeLensReferences?> ExecuteCodeLensAsync(string path, int index) {
		if (await server.CodeLenses.ResolveAsync(path, index).ConfigureAwait(false) is not { Command: { Length: > 0 } id } command) return null;

		if (id == PeekReferencesCommand) {
			if (command.Arguments is not [_, { } position, ..]) return null;

			int line = LspJson.Line(position);
			int column = LspJson.Column(position);

			return new CodeLensReferences(command.Title, line, column, await ReferencesAsync(path, line, column).ConfigureAwait(false));
		}

		await server.Workspace.ExecuteAsync(new ServerCommand(command.Title, id, command.Arguments)).ConfigureAwait(false);
		return null;
	}

	private const string PeekReferencesCommand = "roslyn.client.peekReferences";

	public Task<RenameTarget?> PrepareRenameAsync(string path, int line, int column) => server.Rename.PrepareAsync(path, line, column);

	public Task<WorkspaceEdit?> RenameAsync(string path, int line, int column, string newName) => server.Rename.RenameAsync(path, line, column, newName);

	public LanguageTriggers GetTriggers() {
		ServerTriggers triggers = server.Typing.Triggers;

		return new LanguageTriggers(triggers.Suggest, triggers.Signature, triggers.SignatureRetrigger, settings.FormatOnType ? triggers.OnTypeFormatting : string.Empty, triggers.AutoInsert);
	}

	public async Task<IReadOnlyList<HostTextEdit>> OnTypeFormattingAsync(string path, int line, int column, char character) {
		IReadOnlyList<ServerTextEdit> edits = await server.Typing.OnTypeFormattingAsync(path, line, column, character, settings.TabSize, settings.InsertSpaces).ConfigureAwait(false);

		return [.. edits.Select(static edit => new HostTextEdit(edit.StartLine, edit.StartColumn, edit.EndLine, edit.EndColumn, edit.NewText))];
	}

	public async Task<AutoInsertEdit?> AutoInsertAsync(string path, int line, int column, char character) {
		if (await server.Typing.AutoInsertAsync(path, line, column, character, settings.TabSize, settings.InsertSpaces).ConfigureAwait(false) is not { } insert) return null;

		ServerTextEdit edit = insert.Edit;

		return new AutoInsertEdit(new HostTextEdit(edit.StartLine, edit.StartColumn, edit.EndLine, edit.EndColumn, edit.NewText), insert.IsSnippet);
	}
}
