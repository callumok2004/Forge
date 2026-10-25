namespace Forge.Common.Commands;

public sealed record LanguageTriggers(string Suggest, string Signature, string SignatureRetrigger, string OnTypeFormatting, string AutoInsert)
{
	public static LanguageTriggers Default { get; } = new(".:>", "(,<", ")", string.Empty, string.Empty);
}

public readonly record struct AutoInsertEdit(HostTextEdit Edit, bool IsSnippet);

public enum DocumentHighlightKind
{
	Text = 1,
	Read = 2,
	Write = 3,
}

public enum WorkspaceOperationKind
{
	Edit,
	Create,
	Rename,
	Delete,
}

public sealed record WorkspaceOperation(WorkspaceOperationKind Kind, string Path, IReadOnlyList<HostTextEdit> Edits, string? NewPath = null, bool Overwrite = false, bool IgnoreIfExists = false, bool IgnoreIfNotExists = false, bool Recursive = false);

public sealed record WorkspaceEdit(IReadOnlyList<WorkspaceOperation> Operations)
{
	public bool IsRefactoring { get; init; } = true;

	public bool IsEmpty => Operations.Count == 0;
}

public sealed record DefinitionResult(int OriginLine, int OriginStartColumn, int OriginEndColumn, IReadOnlyList<HostLocation> Locations)
{
	public bool HasOrigin => OriginLine > 0;
}

public readonly record struct EvaluatableExpression(int StartColumn, int EndColumn, string Expression);

public readonly record struct InlineValueLookup(int Line, int StartColumn, int EndColumn, string Name);

public readonly record struct CodeLensEntry(int Index, int Line, int Column, string? Title);

public readonly record struct CodeLensTitle(string Text, bool IsCommand);

public sealed record CodeLensReferences(string Title, int Line, int Column, IReadOnlyList<HostLocation> Locations);

public readonly record struct CodeActionEntry(int Index, string Title, string Kind, bool IsPreferred, string? DisabledReason);

public readonly record struct RenameTarget(int Line, int StartColumn, int EndColumn, string Placeholder);

public readonly record struct DocumentHighlight(int Line, int StartColumn, int EndLine, int EndColumn, DocumentHighlightKind Kind);
