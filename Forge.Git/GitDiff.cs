using Forge.Common.Git;

namespace Forge.Git;

public sealed class GitDiff : IFileDiff
{
	private static readonly GitChange[] None = [];

	private GitChange[] _changes = None;

	private string? _path;

	public IReadOnlyList<GitChange> GetChanges() => _changes;

	public string? GetPath() => _path;

	public void Refresh(IGitRepository repository, string fullPath) {
		_path = fullPath;

		string? output = GitProcess.Run(repository.GetRootPath(),
			"diff", "--no-color", "--no-ext-diff", "-U0", "HEAD", "--", fullPath);

		if (output is null) {
			_changes = None;
			return;
		}

		List<GitChange> changes = [];

		foreach (ReadOnlySpan<char> line in output.AsSpan().EnumerateLines()) {
			if (line.StartsWith("@@")) {
				if (TryParseHunk(line, out GitChange change)) changes.Add(change);
			}
		}

		_changes = changes.Count > 0 ? [.. changes] : None;
	}

	public void Clear() {
		_changes = None;
		_path = null;
	}

	public GitChange? At(int line) {
		GitChange[] changes = _changes;

		int low = 0;
		int high = changes.Length - 1;

		while (low <= high) {
			int middle = (low + high) / 2;
			GitChange change = changes[middle];

			if (line < change.Line) high = middle - 1;
			else if (line >= change.Line + Math.Max(1, change.Count)) low = middle + 1;
			else return change;
		}

		return null;
	}

	private static bool TryParseHunk(ReadOnlySpan<char> header, out GitChange change) {
		change = default;

		int minus = header.IndexOf('-');
		int plus = header.IndexOf('+');

		if (minus < 0 || plus < 0 || plus < minus) return false;

		if (!TryReadRange(header[(minus + 1)..plus], out _, out int oldCount) ||
			!TryReadRange(header[(plus + 1)..], out int newStart, out int newCount)) return false;

		if (newCount == 0) {
			change = new GitChange(newStart, 0, GitChangeKind.Deleted);
			return true;
		}

		change = new GitChange(newStart - 1, newCount,
			oldCount > 0 ? GitChangeKind.Modified : GitChangeKind.Added);

		return true;
	}

	private static bool TryReadRange(ReadOnlySpan<char> text, out int start, out int count) {
		start = 0;
		count = 1;

		int end = 0;
		while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == ',')) end++;

		text = text[..end];

		if (text.IsEmpty) return false;

		int comma = text.IndexOf(',');

		if (comma < 0) return int.TryParse(text, out start);

		return int.TryParse(text[..comma], out start) && int.TryParse(text[(comma + 1)..], out count);
	}
}
