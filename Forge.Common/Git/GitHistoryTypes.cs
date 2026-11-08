namespace Forge.Common.Git;

public enum GitRefKind
{
	Branch,
	Remote,
	Tag,
}

public sealed record GitRef(string Id, string Name, GitRefKind Kind);

public sealed record GitCommit(string Id, string[] Parents, string Author, string Email, long Timestamp, string Subject, string Message, GitRef[] References)
{
	public string ShortId => Id.Length > 7 ? Id[..7] : Id;
}

public sealed record GitHistory(IReadOnlyList<GitCommit> Commits, GitRef? Current, string? CurrentRevision, GitRef? Remote, string? RemoteRevision, string? MergeBase, bool HasMore);

public readonly record struct GitCommitChange(string Path, string? OriginalPath, GitFileStatus Status, int Insertions = 0, int Deletions = 0);
