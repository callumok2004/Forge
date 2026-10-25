using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.TrailingSpaces;

public readonly record struct TrailingRange(int Line, int Start, int End);

public sealed class TrailingSpacesExtension : ForgeExtension
{
	private IOutputChannel? _output;

	private void Log(ICommandHost host, string line) =>
		(_output ??= host.Channel("Trailing Spaces")).Append(line);

	public override string Identifier => "forge.trailing-spaces";

	public override string DisplayName => "Trailing Spaces";

	public override ActivationEvent Activation => ActivationEvent.Startup;

	public bool IncludeEmptyLines { get; set; } = true;

	public bool HighlightCurrentLine { get; set; } = true;

	public bool LiveMatching { get; set; } = true;

	public bool DeleteModifiedLinesOnly { get; set; }

	public bool TrimOnSave { get; set; }

	public string[] SyntaxIgnore { get; set; } = [];

	protected override void Configure(Settings settings) {
		IncludeEmptyLines = settings.GetBoolean("trailing-spaces.includeEmptyLines") ?? IncludeEmptyLines;
		HighlightCurrentLine =
			settings.GetBoolean("trailing-spaces.highlightCurrentLine") ?? HighlightCurrentLine;
		LiveMatching = settings.GetBoolean("trailing-spaces.liveMatching") ?? LiveMatching;
		DeleteModifiedLinesOnly =
			settings.GetBoolean("trailing-spaces.deleteModifiedLinesOnly") ?? DeleteModifiedLinesOnly;
		TrimOnSave = settings.GetBoolean("trailing-spaces.trimOnSave") ?? TrimOnSave;
		SyntaxIgnore = settings.GetStringArray("trailing-spaces.syntaxIgnore") ?? SyntaxIgnore;
	}

	public override void Register(ICommandRegistry commands) {
		commands.Register(new Command("trailing-spaces.deleteTrailingSpaces", "Delete", "Trailing Spaces",
			host => Delete(host, DeleteModifiedLinesOnly)));

		commands.Register(new Command("trailing-spaces.deleteTrailingSpacesModifiedLinesOnly",
			"Delete - Modified Lines Only", "Trailing Spaces", host => Delete(host, modifiedOnly: true)));

		commands.Register(new Command("trailing-spaces.highlightTrailingSpaces", "Highlight",
			"Trailing Spaces", host => {
				if (!host.EditActiveDocument(text => {
					int count = Find(text, skipLine: null, IncludeEmptyLines).Count;

					Log(host, count == 0
						? "none."
						: $"{count} line(s) with trailing whitespace.");

					return null;
				})) host.Refresh();
			}));
	}

	private void Delete(ICommandHost host, bool modifiedOnly) {
		HashSet<int>? modified = modifiedOnly ? ModifiedLines(host) : null;

		bool trimmed = host.EditActiveDocument(text => {
			List<TrailingRange> ranges = Find(text, skipLine: null, includeEmptyLines: true);

			if (modified is not null) ranges.RemoveAll(range => !modified.Contains(range.Line));

			return ranges.Count == 0 ? null : Apply(text, ranges);
		});

		Log(host, trimmed ? "trimmed." : "nothing to trim.");
		host.Refresh();
	}

	private static HashSet<int>? ModifiedLines(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path) return null;

		try {
			string[] saved = File.ReadAllLines(path);
			HashSet<int> modified = [];

			host.EditActiveDocument(text => {
				string[] current = text.Split('\n');

				for (int line = 0; line < current.Length; line++) {
					string now = current[line].TrimEnd('\r');

					if (line >= saved.Length || !string.Equals(now, saved[line], StringComparison.Ordinal)) {
						modified.Add(line);
					}
				}

				return null;
			});

			return modified;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	public static bool FindLine(ReadOnlySpan<char> text, int line, bool includeEmptyLines, out TrailingRange range) {
		int contentEnd = text.Length;

		if (contentEnd > 0 && text[contentEnd - 1] == '\n') contentEnd--;

		int start = contentEnd;

		while (start > 0 && text[start - 1] is ' ' or '	') start--;

		range = new TrailingRange(line, start, contentEnd);
		return start < contentEnd && (includeEmptyLines || start > 0);
	}

	public static List<TrailingRange> Find(string text, int? skipLine, bool includeEmptyLines) {
		List<TrailingRange> ranges = [];

		int line = 0;
		int lineStart = 0;

		for (int i = 0; i <= text.Length; i++) {
			if (i < text.Length && text[i] != '\n') continue;

			int contentEnd = i;

			if (contentEnd > lineStart && text[contentEnd - 1] == '\r') contentEnd--;

			if (line != skipLine) {
				int start = contentEnd;

				while (start > lineStart && (text[start - 1] == ' ' || text[start - 1] == '\t')) start--;

				if (start < contentEnd && (includeEmptyLines || start > lineStart)) {
					ranges.Add(new TrailingRange(line, start - lineStart, contentEnd - lineStart));
				}
			}

			line++;
			lineStart = i + 1;
		}

		return ranges;
	}

	public static string Apply(string text, List<TrailingRange> ranges) {
		System.Text.StringBuilder builder = new(text.Length);

		int line = 0;
		int lineStart = 0;
		int next = 0;

		for (int i = 0; i <= text.Length; i++) {
			if (i < text.Length && text[i] != '\n') continue;

			int contentEnd = i;

			if (contentEnd > lineStart && text[contentEnd - 1] == '\r') contentEnd--;

			int keepTo = contentEnd;

			if (next < ranges.Count && ranges[next].Line == line) {
				keepTo = lineStart + ranges[next].Start;
				next++;
			}

			builder.Append(text, lineStart, keepTo - lineStart);

			builder.Append(text, contentEnd, i - contentEnd);

			if (i < text.Length) builder.Append('\n');

			line++;
			lineStart = i + 1;
		}

		return builder.ToString();
	}
}
