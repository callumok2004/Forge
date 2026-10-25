namespace Forge.Common.Commands;

public interface ILanguageFeatures
{
	bool Handles(string path);

	LanguageTriggers GetTriggers() => LanguageTriggers.Default;

	Task<CompletionList> CompletionsAsync(string path, int line, int column, char trigger, CompletionTrigger kind);

	Task<CompletionDetail?> ResolveCompletionAsync(int index);

	Task<SignatureHelp?> SignatureHelpAsync(string path, int line, int column, char trigger);

	Task<string?> HoverAsync(string path, int line, int column);

	Task<DefinitionResult?> DefinitionAsync(string path, int line, int column) => Task.FromResult<DefinitionResult?>(null);

	Task<IReadOnlyList<HostLocation>> ImplementationsAsync(string path, int line, int column) => Task.FromResult<IReadOnlyList<HostLocation>>([]);

	Task<IReadOnlyList<HostLocation>> ReferencesAsync(string path, int line, int column) => Task.FromResult<IReadOnlyList<HostLocation>>([]);

	Task<IReadOnlyList<HostTextEdit>> OnTypeFormattingAsync(string path, int line, int column, char character) => Task.FromResult<IReadOnlyList<HostTextEdit>>([]);

	Task<IReadOnlyList<DocumentHighlight>> DocumentHighlightsAsync(string path, int line, int column) => Task.FromResult<IReadOnlyList<DocumentHighlight>>([]);

	Task<IReadOnlyList<CodeActionEntry>> CodeActionsAsync(string path, int startLine, int startColumn, int endLine, int endColumn, bool automatic) => Task.FromResult<IReadOnlyList<CodeActionEntry>>([]);

	Task ExecuteCodeActionAsync(int index) => Task.CompletedTask;

	Task<RenameTarget?> PrepareRenameAsync(string path, int line, int column) => Task.FromResult<RenameTarget?>(null);

	Task<WorkspaceEdit?> RenameAsync(string path, int line, int column, string newName) => Task.FromResult<WorkspaceEdit?>(null);

	Task<AutoInsertEdit?> AutoInsertAsync(string path, int line, int column, char character) => Task.FromResult<AutoInsertEdit?>(null);
}
