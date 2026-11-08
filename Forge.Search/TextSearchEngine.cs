using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Forge.Search;

public sealed record TextSearchQuery(string Pattern, bool MatchCase, bool WholeWord, bool IsRegex, string Include, string Exclude, bool UseExcludeSettings)
{
	public IReadOnlyList<string>? SettingsExcludes { get; init; }
}

public readonly record struct TextMatch(int Line, int Column, int Length);

public sealed class FileSearchResult(string path, string relativePath, string[] lines, TextMatch[] matches)
{
	public string Path { get; } = path;

	public string RelativePath { get; } = relativePath;

	public string[] Lines { get; } = lines;

	public TextMatch[] Matches { get; } = matches;
}

public static class TextSearchEngine
{
	public const int MaxResults = 20000;

	private const int MaxFileBytes = 64 * 1024 * 1024;
	private const int BinaryProbe = 8000;
	private const string WordSeparators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";

	private static readonly string SeparatorClass = string.Concat(WordSeparators.Select(static character => "\\" + character));

	public static readonly string[] DefaultExcludes = ["**/.git", "**/.svn", "**/.hg", "**/.DS_Store", "**/Thumbs.db", "**/node_modules", "**/bower_components", "**/*.code-search"];

	public static Regex? Compile(TextSearchQuery query, out string? error) {
		error = null;

		if (query.Pattern.Length == 0) return null;

		string pattern = query.IsRegex ? query.Pattern : Regex.Escape(query.Pattern);

		if (query.WholeWord) pattern = "(?<![^\\s" + SeparatorClass + "])(?:" + pattern + ")(?![^\\s" + SeparatorClass + "])";

		RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled;

		if (!query.MatchCase) options |= RegexOptions.IgnoreCase;

		try {
			return new Regex(pattern, options, TimeSpan.FromSeconds(2));
		}
		catch (ArgumentException exception) {
			error = exception.Message;
			return null;
		}
	}

	public static string[] ListFiles(string root, bool useIgnoreFiles) {
		if (useIgnoreFiles && Git.GitProcess.Run(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard") is { } output) {
			string[] files = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
			return files;
		}

		List<string> found = [];

		try {
			foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })) found.Add(System.IO.Path.GetRelativePath(root, file).Replace('\\', '/'));
		}
		catch (IOException) {
		}

		return [.. found];
	}

	public static int Run(string root, TextSearchQuery query, Regex regex, Action<FileSearchResult> found, CancellationToken cancellation) {
		GlobSet include = GlobSet.Parse(query.Include);
		GlobSet exclude = query.UseExcludeSettings ? GlobSet.Create([.. query.SettingsExcludes ?? DefaultExcludes, .. Split(query.Exclude)]) : GlobSet.Parse(query.Exclude);
		string[] files = ListFiles(root, query.UseExcludeSettings);
		string? literal = !query.IsRegex && !query.WholeWord ? query.Pattern : null;
		StringComparison comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		int total = 0;

		try {
			Parallel.ForEach(Partitioner.Create(0, files.Length, Math.Max(16, files.Length / (Environment.ProcessorCount * 8))), new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Environment.ProcessorCount }, (range, state) => {
				List<TextMatch> matches = [];

				for (int i = range.Item1; i < range.Item2; i++) {
					if (Volatile.Read(ref total) >= MaxResults || cancellation.IsCancellationRequested) {
						state.Stop();
						return;
					}

					string relative = files[i];

					if (exclude.Matches(relative) || (!include.IsEmpty && !include.Matches(relative))) continue;

					string path = System.IO.Path.Combine(root, relative);

					if (SearchFile(path, relative, regex, literal, comparison, matches) is not { } result) continue;

					Interlocked.Add(ref total, result.Matches.Length);
					found(result);
				}
			});
		}
		catch (OperationCanceledException) {
		}

		return total;
	}

	private static IEnumerable<string> Split(string patterns) => patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private static FileSearchResult? SearchFile(string path, string relative, Regex regex, string? literal, StringComparison comparison, List<TextMatch> matches) {
		byte[]? bytes = null;
		char[]? chars = null;

		try {
			using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
			long length = RandomAccess.GetLength(handle);

			if (length == 0 || length > MaxFileBytes) return null;

			bytes = ArrayPool<byte>.Shared.Rent((int)length);
			int read = 0;

			while (read < length) {
				int count = RandomAccess.Read(handle, bytes.AsSpan(read, (int)length - read), read);

				if (count <= 0) break;

				read += count;
			}

			ReadOnlySpan<byte> data = bytes.AsSpan(0, read);
			Encoding encoding = Detect(ref data);

			if (encoding == Encoding.UTF8 && data[..Math.Min(data.Length, BinaryProbe)].Contains((byte)0)) return null;

			chars = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(data.Length));
			int charCount = encoding.GetChars(data, chars);
			ReadOnlySpan<char> text = chars.AsSpan(0, charCount);

			matches.Clear();

			if (literal is not null) FindLiteral(text, literal, comparison, matches);
			else FindRegex(text, regex, matches);

			if (matches.Count == 0) return null;

			return new FileSearchResult(path, relative, CollectLines(text, matches), [.. matches]);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) {
			return null;
		}
		finally {
			if (bytes is not null) ArrayPool<byte>.Shared.Return(bytes);
			if (chars is not null) ArrayPool<char>.Shared.Return(chars);
		}
	}

	private static Encoding Detect(ref ReadOnlySpan<byte> data) {
		if (data is [0xEF, 0xBB, 0xBF, ..]) {
			data = data[3..];
			return Encoding.UTF8;
		}

		if (data is [0xFF, 0xFE, ..]) {
			data = data[2..];
			return Encoding.Unicode;
		}

		if (data is [0xFE, 0xFF, ..]) {
			data = data[2..];
			return Encoding.BigEndianUnicode;
		}

		return Encoding.UTF8;
	}

	private static void FindLiteral(ReadOnlySpan<char> text, string literal, StringComparison comparison, List<TextMatch> matches) {
		int line = 0;
		int lineStart = 0;
		int scanned = 0;
		int position = 0;

		while (position <= text.Length - literal.Length) {
			int hit = text[position..].IndexOf(literal, comparison);

			if (hit < 0) break;

			int start = position + hit;
			Advance(text, ref scanned, start, ref line, ref lineStart);
			AddMatch(text, start, literal.Length, ref scanned, ref line, ref lineStart, matches);
			position = start + Math.Max(1, literal.Length);

			if (matches.Count >= MaxResults) return;
		}
	}

	private static void FindRegex(ReadOnlySpan<char> text, Regex regex, List<TextMatch> matches) {
		int line = 0;
		int lineStart = 0;
		int scanned = 0;

		foreach (ValueMatch match in regex.EnumerateMatches(text)) {
			if (match.Length == 0) continue;

			Advance(text, ref scanned, match.Index, ref line, ref lineStart);
			AddMatch(text, match.Index, match.Length, ref scanned, ref line, ref lineStart, matches);

			if (matches.Count >= MaxResults) return;
		}
	}

	private static void AddMatch(ReadOnlySpan<char> text, int start, int length, ref int scanned, ref int line, ref int lineStart, List<TextMatch> matches) {
		int end = start + length;

		while (true) {
			int newline = text[start..end].IndexOf('\n');
			int stop = newline < 0 ? end : start + newline;
			int column = start - lineStart;
			int span = stop - start;

			if (span > 0 && text[stop - 1] == '\r' && newline >= 0) span--;

			matches.Add(new TextMatch(line, column, span));

			if (newline < 0) break;

			start = stop + 1;
			Advance(text, ref scanned, start, ref line, ref lineStart);

			if (start >= end) break;
		}
	}

	private static void Advance(ReadOnlySpan<char> text, ref int scanned, int target, ref int line, ref int lineStart) {
		while (scanned < target) {
			int newline = text[scanned..target].IndexOf('\n');

			if (newline < 0) {
				scanned = target;
				return;
			}

			scanned += newline + 1;
			line++;
			lineStart = scanned;
		}
	}

	private static string[] CollectLines(ReadOnlySpan<char> text, List<TextMatch> matches) {
		string[] lines = new string[matches.Count];
		int previousLine = -1;
		string previous = string.Empty;
		int line = 0;
		int lineStart = 0;
		int scanned = 0;

		for (int i = 0; i < matches.Count; i++) {
			TextMatch match = matches[i];

			if (match.Line != previousLine) {
				while (line < match.Line) {
					int newline = text[scanned..].IndexOf('\n');

					if (newline < 0) break;

					scanned += newline + 1;
					lineStart = scanned;
					line++;
				}

				ReadOnlySpan<char> rest = text[lineStart..];
				int end = rest.IndexOf('\n');
				ReadOnlySpan<char> content = end < 0 ? rest : rest[..end];
				previous = content.TrimEnd('\r').ToString();
				previousLine = match.Line;
			}

			lines[i] = previous;
		}

		return lines;
	}
}
