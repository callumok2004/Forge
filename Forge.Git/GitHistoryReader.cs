using Forge.Common.Git;

namespace Forge.Git;

public static class GitHistoryReader
{
	private const char Field = '\u001f';

	public static GitHistory? Load(string root, int limit) {
		string? currentName = GitProcess.Run(root, "symbolic-ref", "-q", "HEAD")?.Trim();
		string? upstreamName = GitProcess.Run(root, "rev-parse", "--symbolic-full-name", "@{u}")?.Trim();
		string? head = GitProcess.Run(root, "rev-parse", "HEAD")?.Trim();

		if (head is not { Length: > 0 }) return new GitHistory([], null, null, null, null, null, false);

		GitRef? current = currentName is { Length: > 0 } ? new GitRef(currentName, ShortName(currentName), GitRefKind.Branch) : null;
		GitRef? remote = upstreamName is { Length: > 0 } ? new GitRef(upstreamName, ShortName(upstreamName), GitRefKind.Remote) : null;
		string? remoteRevision = remote is null ? null : GitProcess.Run(root, "rev-parse", remote.Id)?.Trim();
		string? mergeBase = remote is null ? null : GitProcess.Run(root, "merge-base", head, remote.Id)?.Trim();

		List<string> arguments = ["log", "-z", "--topo-order", "--decorate=full", "--format=%H%x1f%P%x1f%aN%x1f%aE%x1f%at%x1f%D%x1f%B", "-n", (limit + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "HEAD"];

		if (remote is not null) arguments.Add(remote.Id);

		arguments.Add("--");

		string? output = GitProcess.Run(root, [.. arguments]);

		if (output is null) return null;

		List<GitCommit> commits = Parse(output);
		bool more = commits.Count > limit;

		if (more) commits.RemoveRange(limit, commits.Count - limit);

		return new GitHistory(commits, current, head, remote, remoteRevision, mergeBase, more);
	}

	private static List<GitCommit> Parse(string output) {
		List<GitCommit> commits = [];

		foreach (string record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
			string[] fields = record.TrimStart('\n').Split(Field, 7);

			if (fields.Length < 7) continue;

			string message = fields[6].TrimEnd();
			int newline = message.IndexOf('\n');
			string subject = newline < 0 ? message : message[..newline].TrimEnd('\r');
			long.TryParse(fields[4], out long timestamp);

			commits.Add(new GitCommit(fields[0], fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[2], fields[3], timestamp, subject, message, ParseReferences(fields[5])));
		}

		return commits;
	}

	private static GitRef[] ParseReferences(string decorations) {
		if (decorations.Length == 0) return [];

		List<GitRef> references = [];

		foreach (string raw in decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries)) {
			string name = raw.StartsWith("HEAD -> ", StringComparison.Ordinal) ? raw[8..] : raw.StartsWith("tag: ", StringComparison.Ordinal) ? raw[5..] : raw;

			if (name == "HEAD" || name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;

			if (name.StartsWith("refs/heads/", StringComparison.Ordinal)) references.Add(new GitRef(name, name[11..], GitRefKind.Branch));
			else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal)) references.Add(new GitRef(name, name[13..], GitRefKind.Remote));
			else if (name.StartsWith("refs/tags/", StringComparison.Ordinal)) references.Add(new GitRef(name, name[10..], GitRefKind.Tag));
		}

		return [.. references];
	}

	private static string ShortName(string reference) {
		foreach (string prefix in (ReadOnlySpan<string>)["refs/heads/", "refs/remotes/", "refs/tags/"]) {
			if (reference.StartsWith(prefix, StringComparison.Ordinal)) return reference[prefix.Length..];
		}

		return reference;
	}

	public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

	public static IReadOnlyList<GitCommitChange> Changes(string root, GitCommit commit) => Changes(root, commit.Parents.Length > 0 ? commit.Parents[0] : EmptyTree, commit.Id);

	public static IReadOnlyList<GitCommitChange> Changes(string root, string from, string to) {
		string? output = GitProcess.Run(root, "diff", "-z", "--name-status", "-M", from, to, "--");

		if (output is null) return [];

		Dictionary<string, (int Insertions, int Deletions)> stats = Stats(root, from, to);
		List<GitCommitChange> changes = [];
		string[] parts = output.Split('\0');

		for (int i = 0; i + 1 < parts.Length; i++) {
			string status = parts[i];

			if (status.Length == 0) continue;

			if (status[0] is 'R' or 'C' && i + 2 < parts.Length) {
				stats.TryGetValue(parts[i + 2], out (int Insertions, int Deletions) renamed);
				changes.Add(new GitCommitChange(Full(root, parts[i + 2]), Full(root, parts[i + 1]), GitFileStatus.Renamed, renamed.Insertions, renamed.Deletions));
				i += 2;
				continue;
			}

			GitFileStatus kind = status[0] switch {
				'A' => GitFileStatus.Added,
				'D' => GitFileStatus.Deleted,
				_ => GitFileStatus.Modified,
			};

			stats.TryGetValue(parts[i + 1], out (int Insertions, int Deletions) counts);
			changes.Add(new GitCommitChange(Full(root, parts[i + 1]), null, kind, counts.Insertions, counts.Deletions));
			i++;
		}

		return changes;
	}

	private static Dictionary<string, (int Insertions, int Deletions)> Stats(string root, string from, string to) {
		Dictionary<string, (int, int)> stats = new(StringComparer.Ordinal);
		string? output = GitProcess.Run(root, "diff", "-z", "--numstat", "-M", from, to, "--");

		if (output is null) return stats;

		string[] parts = output.Split('\0');

		for (int i = 0; i < parts.Length; i++) {
			string record = parts[i];
			string[] fields = record.Split('\t', 3);

			if (fields.Length < 3) continue;

			int.TryParse(fields[0], out int insertions);
			int.TryParse(fields[1], out int deletions);

			if (fields[2].Length == 0 && i + 2 < parts.Length) {
				stats[parts[i + 2]] = (insertions, deletions);
				i += 2;
				continue;
			}

			stats[fields[2]] = (insertions, deletions);
		}

		return stats;
	}

	public static string? Show(string root, string revision, string relative) {
		string? text = GitProcess.Run(root, "show", revision + ":" + relative);
		return text is ['﻿', ..] ? text[1..] : text;
	}

	private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
