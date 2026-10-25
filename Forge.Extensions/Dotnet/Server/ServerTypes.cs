namespace Forge.Extensions.Dotnet.Server;

public readonly record struct ServerLocation(string Path, int Line, int Column, string Preview);

public readonly record struct ServerSymbol(string Name, int Kind, int Depth, int Line, int Column);

public readonly record struct ServerTextEdit(int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText);

public readonly record struct ProjectContext(string Id, string Label, bool IsMiscellaneous);

public sealed record ProjectContexts(IReadOnlyList<ProjectContext> All, int DefaultIndex)
{
	public string? SelectedId { get; init; }

	public ProjectContext? Current {
		get {
			foreach (ProjectContext context in All) {
				if (context.Id == SelectedId) return context;
			}

			return DefaultIndex < All.Count ? All[DefaultIndex] : null;
		}
	}
}

public readonly record struct ServerToken(int Line, int StartColumn, int Length, string Type);

public readonly record struct ServerCompletion(int Index, string Label, string LabelDetail, string LabelDescription, string InsertText, string Detail, string Kind, string SortText, string FilterText, bool Preselect, int ReplaceLine, int ReplaceStartColumn, int ReplaceEndColumn);

public readonly record struct ServerCompletionList(IReadOnlyList<ServerCompletion> Items, bool IsIncomplete);

public readonly record struct ServerCompletionDetail(string Detail, string Documentation, IReadOnlyList<ServerTextEdit> AdditionalEdits);

public readonly record struct ServerSignature(string Label, IReadOnlyList<(int Start, int Length)> Parameters, string Documentation, int ActiveParameter);

public sealed record ServerSignatures(IReadOnlyList<ServerSignature> Overloads, int ActiveIndex, int ActiveParameter)
{
	public ServerSignature Active => Overloads[Math.Clamp(ActiveIndex, 0, Overloads.Count - 1)];

	public int ParameterIndex => Active.ActiveParameter >= 0 ? Active.ActiveParameter : ActiveParameter;
}

public readonly record struct ServerDiagnostic(string File, int Line, int Column, string Message, bool IsError);

public enum LanguageServerState
{
	Stopped,
	Starting,
	Running,
	Failed,
}
