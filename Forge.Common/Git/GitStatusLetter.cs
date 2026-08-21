namespace Forge.Common.Git;

public static class GitStatusLetter
{
	public static string Of(GitFileStatus status) => status switch {
		GitFileStatus.Modified => "M",
		GitFileStatus.Added => "A",
		GitFileStatus.Deleted => "D",
		GitFileStatus.Renamed => "R",
		GitFileStatus.Untracked => "U",
		GitFileStatus.Ignored => "I",
		GitFileStatus.Conflicted => "C",
		_ => string.Empty,
	};
}
