using System.Text.RegularExpressions;

using Forge.Text;

namespace Forge.Editor;

internal readonly record struct MinimapSectionHeader(int StartLine, int StartColumn, int EndLine, int EndColumn, string Text, bool HasSeparatorLine);

internal static partial class MinimapSectionHeaders
{
	private const int ChunkSize = 100;
	private const int MaxSectionLines = 5;

	private static readonly Dictionary<string, string> RegionStarts = new(StringComparer.Ordinal) {
		["bat"] = @"^\s*(::|@?[Rr][Ee][Mm])\s*#region",
		["coffeescript"] = @"^\s*#region\b",
		["c"] = @"^\s*#\s*pragma\s+region\b",
		["cpp"] = @"^\s*#\s*pragma\s+region\b",
		["csharp"] = @"^\s*#region\b",
		["css"] = @"^\s*\/\*\s*#region\b\s*(.*?)\s*\*\/",
		["fsharp"] = @"^\s*//\s*#region\b|^\s*\(\*\s*#region(.*)\*\)",
		["go"] = @"^\s*//\s*#?region\b",
		["html"] = @"^\s*<!--\s*#region\b.*-->",
		["java"] = @"^\s*//\s*(?:(?:#?region\b)|(?:<editor-fold\b))",
		["javascript"] = @"^\s*//\s*#?region\b",
		["javascriptreact"] = @"^\s*//\s*#?region\b",
		["julia"] = @"^\s*#region",
		["less"] = @"^\s*\/\*\s*#region\b\s*(.*?)\s*\*\/",
		["markdown"] = @"^\s*<!--\s*#?region\b.*-->",
		["powershell"] = @"^\s*#[rR]egion\b",
		["python"] = @"^\s*#\s*region\b",
		["rust"] = @"^\s*//\s*#?region\b",
		["scss"] = @"^\s*\/\*\s*#region\b\s*(.*?)\s*\*\/",
		["shellscript"] = @"^\s*#\s*#?region\b.*",
		["sql"] = @"^\s*--\s*#region\b",
		["typescript"] = @"^\s*//\s*#?region\b",
		["typescriptreact"] = @"^\s*//\s*#?region\b",
		["vb"] = @"^\s*#Region\b",
		["yaml"] = @"^\s*#\s*region\b",
	};

	private static readonly Dictionary<string, Regex> Compiled = [];

	[GeneratedRegex(@"^-+|-+$")]
	private static partial Regex TrimDashes();

	private static Regex? RegionStart(string? language) {
		if (language is null || !RegionStarts.TryGetValue(language, out string? pattern)) return null;

		if (!Compiled.TryGetValue(pattern, out Regex? regex)) Compiled[pattern] = regex = new Regex(pattern, RegexOptions.ECMAScript);

		return regex;
	}

	public static List<MinimapSectionHeader> Find(TextSnapshot snapshot, string? language, bool region, bool mark, string markRegex, Func<int, int, bool> isComment) {
		List<MinimapSectionHeader> headers = [];

		if (region && RegionStart(language) is { } start) CollectRegionHeaders(snapshot, start, headers);

		if (mark) CollectMarkHeaders(snapshot, markRegex, headers, isComment);

		return headers;
	}

	private static void CollectRegionHeaders(TextSnapshot snapshot, Regex start, List<MinimapSectionHeader> headers) {
		for (int line = 0; line < snapshot.LineCount; line++) {
			string content = snapshot.GetText(snapshot.GetLineSpan(line));
			Match match = start.Match(content);

			if (!match.Success) continue;

			int startColumn = match.Length + 1;
			int endColumn = content.Length + 1;

			if (endColumn <= startColumn) continue;

			(string text, bool separator) = HeaderText(content[match.Length..]);

			if (text.Length > 0 || separator) headers.Add(new MinimapSectionHeader(line + 1, startColumn, line + 1, endColumn, text, separator));
		}
	}

	private static void CollectMarkHeaders(TextSnapshot snapshot, string pattern, List<MinimapSectionHeader> headers, Func<int, int, bool> isComment) {
		if (string.IsNullOrWhiteSpace(pattern)) return;

		bool multiline = IsMultilineRegexSource(pattern);
		Regex regex;

		try {
			regex = new Regex(pattern, RegexOptions.Multiline | (multiline ? RegexOptions.Singleline : RegexOptions.None));
		}
		catch (ArgumentException) {
			return;
		}

		if (regex.IsMatch(string.Empty)) return;

		int lineCount = snapshot.LineCount;
		List<MinimapSectionHeader> found = [];

		for (int startLine = 1; startLine <= lineCount; startLine += ChunkSize - MaxSectionLines) {
			int endLine = Math.Min(startLine + ChunkSize - 1, lineCount);
			System.Text.StringBuilder builder = new();

			for (int i = startLine; i <= endLine; i++) {
				if (i > startLine) builder.Append('\n');

				builder.Append(snapshot.GetText(snapshot.GetLineSpan(i - 1)));
			}

			string text = builder.ToString();

			foreach (Match match in regex.Matches(text)) {
				if (match.Length == 0) continue;

				int lineOffset = 0;

				for (int i = 0; i < match.Index; i++) {
					if (text[i] == '\n') lineOffset++;
				}

				int lineNumber = startLine + lineOffset;
				string[] matchLines = match.Value.Split('\n');
				int matchEndLine = lineNumber + matchLines.Length - 1;
				int lineStartIndex = text.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;

				if (match.Index == 0) lineStartIndex = 0;

				int startColumn = match.Index - lineStartIndex + 1;
				int endColumn = matchLines.Length == 1 ? startColumn + match.Length : matchLines[^1].Length + 1;
				string label = match.Groups["label"].Success ? match.Groups["label"].Value : string.Empty;
				bool separator = match.Groups["separator"].Success && match.Groups["separator"].Value.Length > 0;

				if (label.Length == 0 && !separator) continue;

				if (found.Count == 0 || found[^1].EndLine < lineNumber) found.Add(new MinimapSectionHeader(lineNumber, startColumn, matchEndLine, endColumn, label, separator));
			}
		}

		foreach (MinimapSectionHeader header in found) {
			if (isComment(header.StartLine - 1, header.StartColumn - 1)) headers.Add(header);
		}
	}

	private static bool IsMultilineRegexSource(string source) {
		for (int i = 0; i < source.Length; i++) {
			if (source[i] == '\n') return true;

			if (source[i] != '\\') continue;

			i++;

			if (i >= source.Length) break;

			if (source[i] is 'n' or 'r' or 'W') return true;
		}

		return false;
	}

	private static (string Text, bool Separator) HeaderText(string text) {
		text = text.Trim();
		bool separator = text.StartsWith('-');
		return (TrimDashes().Replace(text, string.Empty), separator);
	}
}
