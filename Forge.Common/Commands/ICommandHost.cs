namespace Forge.Common.Commands;

public interface ICommandHost
{
	string? GetWorkspacePath();
	string? GetActiveFilePath();

	int GetCaretLine();
	int GetCaretColumn();
	int GetLineCount();
	int GetErrorCount();

	string? GetBranchName();
	string? GetRemoteUrl();

	void Log(string line);
	IOutputChannel Channel(string name);

	void Refresh();
	void OpenFile(string path);
	void GoTo(string path, int line, int column);
	void ShowReferences(string title, IReadOnlyList<HostLocation> locations);

	bool EditActiveDocument(Func<string, string?> transform);
	string? GetDocumentText(string path);
	Task<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit);
	void SetSemanticTokens(string path, IReadOnlyList<SemanticToken> tokens);
	void ReportProblems(string owner, bool build, IReadOnlyList<Diagnostic> problems);

	void SetExplorerGroups(IReadOnlyList<Forge.Common.Workspace.ExplorerGroup>? groups);

	void ShowPanel(string view);
	void ShowCommandPalette();
	void ShowOutput(IOutputChannel channel);
	void ShowQuickPick(string placeholder, IReadOnlyList<QuickPickItem> items, Action<int> picked);
	void ShowMessage(MessageSeverity severity, string message, IReadOnlyList<string> actions, Action<int>? chosen = null);

	void RunInTerminal(string name, string commandLine, string? workingDirectory, Action<string>? lines, Action<int>? finished);

	void SetStatusItem(string id, string icon, string text, string? commandId, bool rightAligned = false, int priority = 0);
	void RemoveStatusItem(string id);

	void ObserveDocuments(Action<DocumentEvent, string, string?> observer);
	void ProvideLanguageFeatures(ILanguageFeatures features);

	ILanguageFeatures? GetLanguageFeatures() => null;

	void SetLineHints(string owner, IReadOnlyDictionary<string, IReadOnlyDictionary<int, Editor.LineHint>>? byPath) { }
}
