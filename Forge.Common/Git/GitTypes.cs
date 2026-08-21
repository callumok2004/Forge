namespace Forge.Common.Git;

public enum GitFileStatus
{
	Unchanged,

	Modified,
	Added,
	Deleted,
	Renamed,
	Conflicted,

	Untracked,

	Ignored,
}

public readonly record struct GitEntry(string Path, GitFileStatus Status);

public enum GitChangeKind
{
	Added,
	Modified,
	Deleted,
}

public readonly record struct GitChange(int Line, int Count, GitChangeKind Kind);
