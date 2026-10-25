using Forge.Common.Git;
using Forge.Common.Render;

namespace Forge.Workbench;

public enum ScmRowKind : byte
{
	Group,
	Folder,
	File,
}

public enum ScmGroup : byte
{
	Merge,
	Staged,
	Changes,
}

public readonly record struct ScmRow(ScmRowKind Kind, ScmGroup Group, int Depth, string Label, string Key, GitEntry Entry, int Count, ScmRowAction[] Actions)
{
	public bool Staged => Group == ScmGroup.Staged;
}

public sealed record ScmRowAction(Codicon Icon, string Title, Action<ScmTree, ScmRow> Run)
{
	public static readonly ScmRowAction OpenFile = new(Codicon.GoToFile, "Open File", static (tree, row) => tree.Open(row));

	public static readonly ScmRowAction Stage = new(Codicon.Add, "Stage Changes", static (tree, row) => tree.Stage(row));

	public static readonly ScmRowAction Unstage = new(Codicon.Remove, "Unstage Changes", static (tree, row) => tree.Unstage(row));

	public static readonly ScmRowAction Discard = new(Codicon.Discard, "Discard Changes", static (tree, row) => tree.Discard(row));

	public static readonly ScmRowAction StageAll = new(Codicon.Add, "Stage All Changes", static (tree, _) => tree.PostCommand("git.stageAll"));

	public static readonly ScmRowAction StageAllMerge = new(Codicon.Add, "Stage All Merge Changes", static (tree, _) => tree.PostCommand("git.stageAllMerge"));

	public static readonly ScmRowAction UnstageAll = new(Codicon.Remove, "Unstage All Changes", static (tree, _) => tree.PostCommand("git.unstageAll"));

	public static readonly ScmRowAction DiscardAll = new(Codicon.Discard, "Discard All Changes", static (tree, _) => tree.PostCommand("git.cleanAll"));

	public static readonly ScmRowAction[] ChangesGroup = [DiscardAll, StageAll];

	public static readonly ScmRowAction[] StagedGroup = [UnstageAll];

	public static readonly ScmRowAction[] MergeGroup = [StageAllMerge];

	public static readonly ScmRowAction[] ChangedFile = [OpenFile, Discard, Stage];

	public static readonly ScmRowAction[] StagedFile = [OpenFile, Unstage];

	public static readonly ScmRowAction[] ChangedFolder = [Discard, Stage];

	public static readonly ScmRowAction[] StagedFolder = [Unstage];
}
