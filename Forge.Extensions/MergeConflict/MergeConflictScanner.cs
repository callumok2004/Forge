namespace Forge.Extensions.MergeConflict;

public readonly record struct MergeConflict(int CurrentStart, int CurrentEnd, int SplitterLine, int IncomingStart, int IncomingEnd, string CurrentLabel, string IncomingLabel);

public static class MergeConflictScanner
{
	private const string CurrentMarker = "<<<<<<<";
	private const string SplitterMarker = "=======";
	private const string IncomingMarker = ">>>>>>>";

	public static List<MergeConflict> Find(string text) {
		List<MergeConflict> conflicts = [];

		string[] lines = text.ReplaceLineEndings("\n").Split('\n');

		int current = -1;
		int splitter = -1;
		string currentLabel = string.Empty;

		for (int i = 0; i < lines.Length; i++) {
			string line = lines[i];

			if (line.StartsWith(CurrentMarker, StringComparison.Ordinal)) {
				current = i;
				splitter = -1;
				currentLabel = Label(line, CurrentMarker);
				continue;
			}

			if (current < 0) continue;

			if (line.StartsWith(SplitterMarker, StringComparison.Ordinal)) {
				splitter = i;
				continue;
			}

			if (!line.StartsWith(IncomingMarker, StringComparison.Ordinal) || splitter < 0) continue;

			conflicts.Add(new MergeConflict(current, splitter - 1, splitter, splitter + 1, i - 1, currentLabel, Label(line, IncomingMarker)));

			current = -1;
			splitter = -1;
		}

		return conflicts;
	}

	public static string Resolve(string text, MergeConflict conflict, MergeResolution resolution) {
		string[] lines = text.ReplaceLineEndings("\n").Split('\n');
		List<string> kept = [with(lines.Length)];

		for (int i = 0; i < lines.Length; i++) {
			if (i == conflict.CurrentStart || i == conflict.SplitterLine || i == conflict.IncomingEnd + 1) continue;

			bool inCurrent = i >= conflict.CurrentStart + 1 && i <= conflict.CurrentEnd;
			bool inIncoming = i >= conflict.IncomingStart && i <= conflict.IncomingEnd;

			if (inCurrent && resolution == MergeResolution.Incoming) continue;

			if (inIncoming && resolution == MergeResolution.Current) continue;

			kept.Add(lines[i]);
		}

		return string.Join(Environment.NewLine, kept);
	}

	public static string ResolveAll(string text, MergeResolution resolution) {
		string result = text;

		while (Find(result) is { Count: > 0 } conflicts) result = Resolve(result, conflicts[0], resolution);

		return result;
	}

	private static string Label(string line, string marker) =>
		line.Length > marker.Length ? line[marker.Length..].Trim() : string.Empty;
}

public enum MergeResolution
{
	Current,
	Incoming,
	Both,
}
