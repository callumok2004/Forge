using System.Globalization;

namespace Forge.Git;

public sealed class GitBlame
{
	private const int MessageLimit = 50;

	private readonly record struct Commit(string Author, string Email, long Time, string Summary, bool Uncommitted);

	private readonly Commit[] _lines;

	private GitBlame(Commit[] lines) => _lines = lines;

	public static GitBlame? Run(string repositoryRoot, string fullPath, string contents) {
		string? output = GitProcess.RunWithInput(repositoryRoot, contents, "blame", "--root", "--incremental", "--contents", "-", "--", fullPath);

		if (output is null) return null;

		string? name = GitProcess.Run(repositoryRoot, "config", "user.name")?.Trim();
		string? email = GitProcess.Run(repositoryRoot, "config", "user.email")?.Trim();
		int lineCount = contents.Count('\n') + 1;
		Commit[] lines = new Commit[lineCount];
		Dictionary<string, Commit> commits = [];
		ReadOnlySpan<char> text = output;
		string sha = string.Empty;
		int final = 0;
		int count = 0;
		string author = string.Empty, mail = string.Empty, summary = string.Empty;
		long time = 0;
		bool header = true;
		Span<Range> parts = stackalloc Range[5];

		foreach (Range range in text.Split('\n')) {
			ReadOnlySpan<char> line = text[range];

			if (line.Length == 0) continue;

			if (header) {
				if (line.Split(parts, ' ') < 4) continue;

				sha = new string(line[parts[0]]);
				final = int.Parse(line[parts[2]], CultureInfo.InvariantCulture);
				count = int.Parse(line[parts[3]], CultureInfo.InvariantCulture);
				header = false;

				if (commits.TryGetValue(sha, out Commit known)) {
					author = known.Author;
					mail = known.Email;
					time = known.Time;
					summary = known.Summary;
				}

				continue;
			}

			int space = line.IndexOf(' ');
			ReadOnlySpan<char> key = space < 0 ? line : line[..space];
			ReadOnlySpan<char> value = space < 0 ? [] : line[(space + 1)..];

			switch (key) {
				case "author": author = new string(value); break;
				case "author-mail": mail = new string(value.Trim("<>")); break;
				case "author-time": time = long.Parse(value, CultureInfo.InvariantCulture); break;
				case "summary": summary = new string(value); break;
				case "filename": {
					bool uncommitted = sha.All(static c => c == '0');
					Commit commit = uncommitted ? new Commit(name ?? author, email ?? mail, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Uncommitted changes", true) : new Commit(author, mail, time, summary, false);
					commits[sha] = commit;

					for (int i = 0; i < count; i++) {
						int index = final - 1 + i;

						if (index >= 0 && index < lines.Length) lines[index] = commit;
					}

					header = true;
					break;
				}
			}
		}

		for (int i = 0; i < lines.Length; i++) {
			Commit commit = lines[i];

			if (commit.Author is null) continue;

			bool current = (name is null || commit.Author == name) && (email is null || commit.Email == email) && (name is not null || email is not null);
			lines[i] = commit with { Author = current || commit.Uncommitted ? "You" : commit.Author };
		}

		return new GitBlame(lines);
	}

	public string? Format(int line, DateTimeOffset now) {
		if (line < 0 || line >= _lines.Length || _lines[line].Author is not { } author) return null;

		Commit commit = _lines[line];
		DateTimeOffset date = DateTimeOffset.FromUnixTimeSeconds(commit.Time);
		string message = commit.Summary;

		if (message.Length > MessageLimit) message = string.Concat(message.AsSpan(0, MessageLimit - 1), "…");

		return author + ", " + Relative(date, now) + " • " + message;
	}

	private static readonly (string Unit, double Threshold, double Divisor)[] Units = [
		("year", 629856e5, 31536e6),
		("month", 2628e6, 2628e6),
		("week", 6048e5, 6048e5),
		("day", 864e5, 864e5),
		("hour", 36e5, 36e5),
		("minute", 6e4, 6e4),
		("second", 1e3, 1e3),
	];

	public static string Relative(DateTimeOffset date, DateTimeOffset now) {
		double delta = (date - now).TotalMilliseconds;

		foreach ((string unit, double threshold, double divisor) in Units) {
			if (Math.Abs(delta) < threshold && divisor != 1e3) continue;

			long value = (long)Math.Truncate(delta / divisor);

			return (unit, value) switch {
				("second", 0) => "now",
				("day", -1) => "yesterday",
				("day", 1) => "tomorrow",
				(_, -1) when unit is "week" or "month" or "year" => "last " + unit,
				(_, 1) when unit is "week" or "month" or "year" => "next " + unit,
				(_, < 0) => -value + " " + unit + (value == -1 ? string.Empty : "s") + " ago",
				_ => "in " + value + " " + unit + (value == 1 ? string.Empty : "s"),
			};
		}

		return string.Empty;
	}
}
