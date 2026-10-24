using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Editor;
using Forge.Language;
using Forge.Syntax;
using Forge.Workspace;

namespace Forge.Shell;

public sealed class CommandHost(Workbench workbench, Action refresh) : ICommandHost
{
	public string? GetWorkspacePath() => workbench.RootPath;

	public string? GetActiveFilePath() => workbench.ActiveDocument?.Path;

	public int GetCaretLine() => (workbench.ActiveView?.CaretLine ?? 0) + 1;

	public int GetCaretColumn() => workbench.ActiveView is { } view ? view.VisualColumnAt(view.CaretLine, view.CaretOffset) + 1 : 0;

	public int GetLineCount() => workbench.ActiveView?.Snapshot.LineCount ?? 0;

	public int GetErrorCount() => workbench.StatusBar.Errors;

	public string? GetBranchName() => workbench.BranchName;

	public string? GetRemoteUrl() => workbench.TitleBar.RemoteOwner is { } owner && workbench.TitleBar.RemoteRepository is { } repository
		? $"https://github.com/{owner}/{repository}"
		: null;

	public void Log(string line) => workbench.Panel.Output.Append(line);

	public IOutputChannel Channel(string name) => workbench.Panel.Channel(name);

	public void ShowOutput(IOutputChannel channel) {
		workbench.Panel.Show(PanelView.Output);
		workbench.Panel.ActivateChannel(Channels.All().ToList().IndexOf(channel));
	}

	public void Refresh() => refresh();

	public void OpenFile(string path) => workbench.OpenFile(path);

	public void GoTo(string path, int line, int column) => workbench.GoTo(path, line, column);

	public void SetSemanticTokens(string path, IReadOnlyList<SemanticToken> tokens) => workbench.SetSemanticTokens(path, tokens);

	public void ShowReferences(string title, IReadOnlyList<HostLocation> locations) => workbench.Panel.SetReferences(title, locations);

	public bool EditActiveDocument(Func<string, string?> transform) => workbench.EditActiveDocument(transform);

	public void RunInTerminal(string name, string commandLine, string? workingDirectory,
		Action<string>? lines, Action<int>? finished) =>
		workbench.Panel.RunTask(name, commandLine, workingDirectory, lines, finished);

	public void ReportProblems(IReadOnlyList<Diagnostic> problems) =>
		workbench.Panel.SetProblems(problems.Select(static problem =>
			new Problem(problem.File, problem.Line, problem.Column, problem.Message, problem.IsError)));

	public void SetStatusItem(string id, string icon, string text, string? commandId, bool rightAligned, int priority) =>
		workbench.StatusBar.SetItem(new StatusBar.Item(id,
			Enum.TryParse(icon, ignoreCase: true, out Codicon parsedIcon) ? parsedIcon : Codicon.Run,
			text, commandId, rightAligned, priority));

	public void RemoveStatusItem(string id) => workbench.StatusBar.RemoveItem(id);

	public void ObserveDocuments(Action<DocumentEvent, string, string?> observer) => workbench.ObserveDocuments(observer);

	public void ProvideLanguageFeatures(ILanguageFeatures features) {
		if (workbench.IntelliSense is { } intelliSense) intelliSense.Features = features;
	}

	public void ShowPanel(string view) {
		if (Enum.TryParse(view.Replace(" ", string.Empty), ignoreCase: true, out PanelView parsed))
			workbench.Panel.Show(parsed);
	}

	public void ShowCommandPalette() => workbench.Palette.Show(Registry);
}
