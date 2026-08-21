using Forge.Common.Git;
using Forge.Theming;

namespace Forge.Git;

public static class GitStatusColor
{
	public static Color? Of(GitFileStatus status) => status switch {
		GitFileStatus.Modified => Theme.GitModifiedForeground,
		GitFileStatus.Renamed => Theme.GitRenamedForeground,
		GitFileStatus.Added => Theme.GitAddedForeground,
		GitFileStatus.Untracked => Theme.GitUntrackedForeground,
		GitFileStatus.Conflicted => Theme.GitConflictForeground,
		GitFileStatus.Deleted => Theme.GitDeletedForeground,
		GitFileStatus.Ignored => Theme.GitIgnoredForeground,
		_ => null,
	};
}
