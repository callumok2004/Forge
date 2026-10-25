namespace Forge.Common.Commands;

public readonly record struct CompletionEntry(int Index, string Label, string LabelDetail, string LabelDescription, string InsertText, string Detail, string Kind, string FilterText, bool Preselect, int ReplaceLine, int ReplaceStartColumn, int ReplaceEndColumn);

public enum CompletionTrigger
{
	Invoked = 1,

	Character = 2,

	Incomplete = 3,
}

public readonly record struct CompletionList(IReadOnlyList<CompletionEntry> Items, bool IsIncomplete);

public readonly record struct HostTextEdit(int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText);

public readonly record struct CompletionDetail(string Detail, string Documentation, IReadOnlyList<HostTextEdit> AdditionalEdits);

public sealed record SignatureHelp(string Label, IReadOnlyList<(int Start, int Length)> Parameters, string Documentation, int ParameterIndex, int OverloadIndex, int OverloadCount);

public readonly record struct HostLocation(string Path, int Line, int Column, string Preview);

public readonly record struct SemanticToken(int Line, int StartColumn, int Length, uint Color);

public enum DocumentEvent
{
	Opened,
	Changed,
	Saved,
	Closed,
}

public readonly record struct QuickPickItem(string Label, string Description = "", string Detail = "");

public enum MessageSeverity
{
	Info,
	Warning,
	Error,
}

public readonly record struct Diagnostic(string File, int Line, int Column, string Message, bool IsError);
