using System.Text.RegularExpressions;

using Forge.Common.Commands;
using Forge.Common.Git;
using Forge.Editor;
using Forge.Git;
using Forge.Text;
using Forge.Workbench;

namespace Forge.Shell;

public sealed partial class EditorLenses
{
	private const int MessageMaxChars = 500;
	private const int BlameDelayAfterEdit = 5000;

	private record struct Tracked(int Start, int End, DiagnosticSeverity Severity, string Message);

	private IReadOnlyList<Problem>? _problems;
	private string? _path;
	private TextBuffer? _buffer;
	private readonly List<Tracked> _tracked = [];
	private bool _dirty;
	private readonly Dictionary<int, TextView.ProblemLens> _lenses = [];
	private readonly List<TextView.Squiggle> _squiggles = [];
	private Func<int, string?>? _blameFor;
	private int _formattedLine = -1;
	private long _formattedMinute = -1;
	private string? _formatted;

	private string? _blamePath;
	private int _blameVersion = -1;
	private int _requestedVersion = -1;
	private GitBlame? _blame;
	private Timer? _timer;

	public Action<Action> Post { get; set; } = static action => action();

	public Action? Changed { get; set; }

	[GeneratedRegex(@"[\n\r\t]+")]
	private static partial Regex Linebreaks();

	public void Apply(TextView view, string? path, IReadOnlyList<Problem> problems, IGitRepository? repository, bool diff) {
		if (!ReferenceEquals(view.Buffer, _buffer)) {
			if (_buffer is not null) _buffer.Changed -= OnChanged;

			_buffer = view.Buffer;
			_buffer.Changed += OnChanged;
			_problems = null;
		}

		if (!ReferenceEquals(problems, _problems) || !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) {
			_problems = problems;
			_path = path;
			Track(view.Snapshot, path, problems);
		}

		if (_dirty) Project(view.Snapshot);

		view.ProblemLenses = diff || _lenses.Count == 0 ? null : _lenses;
		view.Squiggles = _squiggles.Count == 0 ? null : _squiggles;
		ApplyBlame(view, path, repository);
	}

	private void Track(TextSnapshot snapshot, string? path, IReadOnlyList<Problem> problems) {
		_tracked.Clear();
		_dirty = true;

		if (path is null) return;

		foreach (Problem problem in problems) {
			if (!string.Equals(problem.File, path, StringComparison.OrdinalIgnoreCase)) continue;

			int line = Math.Max(0, problem.Line - 1);
			int column = Math.Max(0, problem.Column - 1);
			int start = OffsetOf(snapshot, line, column);
			int end = problem.EndLine > 0 ? OffsetOf(snapshot, problem.EndLine - 1, Math.Max(0, problem.EndColumn - 1)) : start;

			_tracked.Add(new Tracked(start, Math.Max(start, end), problem.Severity, Prepare(problem.Message)));
		}
	}

	private static int OffsetOf(TextSnapshot snapshot, int line, int column) {
		if (line >= snapshot.LineCount) return snapshot.Length;

		return snapshot.GetLineStartOffset(line) + Math.Min(column, snapshot.GetLineLength(line));
	}

	private void OnChanged(TextBuffer buffer, TextChange change) {
		if (_tracked.Count == 0) return;

		int offset = change.Offset;
		int removedEnd = offset + change.RemovedLength;
		int delta = change.Delta;

		for (int i = 0; i < _tracked.Count; i++) {
			Tracked tracked = _tracked[i];
			int start = tracked.Start >= removedEnd ? tracked.Start + delta : tracked.Start > offset ? offset : tracked.Start;
			int end = tracked.End > removedEnd ? tracked.End + delta : tracked.End > offset ? offset : tracked.End;

			_tracked[i] = tracked with { Start = start, End = Math.Max(start, end) };
		}

		_dirty = true;
	}

	private void Project(TextSnapshot snapshot) {
		_dirty = false;
		_lenses.Clear();
		_squiggles.Clear();

		foreach (Tracked tracked in _tracked) {
			int start = Math.Min(tracked.Start, snapshot.Length);
			int end = Math.Min(tracked.End, snapshot.Length);
			int line = snapshot.GetLineAt(start);
			int endLine = snapshot.GetLineAt(end);

			_squiggles.Add(new TextView.Squiggle(line, start - snapshot.GetLineStartOffset(line), endLine, end - snapshot.GetLineStartOffset(endLine), tracked.Severity));

			if (!_lenses.TryGetValue(line, out TextView.ProblemLens existing) || tracked.Severity < existing.Severity) _lenses[line] = new TextView.ProblemLens(tracked.Message, tracked.Severity);
		}
	}

	private static string Prepare(string message) {
		string text = Linebreaks().Replace(message, "⏎");
		int count = 0;

		for (int i = 0; i < text.Length; i++) {
			if (char.IsLowSurrogate(text[i])) continue;

			if (++count > MessageMaxChars) return string.Concat(text.AsSpan(0, i), "…");
		}

		return text;
	}

	private void ApplyBlame(TextView view, string? path, IGitRepository? repository) {
		if (path is null || repository is null) {
			view.BlameFor = null;
			return;
		}

		int version = view.Buffer.Version;

		if (!string.Equals(path, _blamePath, StringComparison.OrdinalIgnoreCase)) {
			_blamePath = path;
			_blame = null;
			_formattedLine = -1;
			_blameVersion = -1;
			_requestedVersion = -1;
			Request(repository.GetRootPath(), path, view, 0);
		}
		else if (version != _blameVersion && version != _requestedVersion) {
			Request(repository.GetRootPath(), path, view, BlameDelayAfterEdit);
		}

		view.BlameFor = _blameVersion == version && _blame is not null ? _blameFor ??= FormatBlame : null;
	}

	private string? FormatBlame(int line) {
		DateTimeOffset now = DateTimeOffset.UtcNow;
		long minute = now.ToUnixTimeSeconds();

		if (line == _formattedLine && minute == _formattedMinute) return _formatted;

		_formattedLine = line;
		_formattedMinute = minute;
		_formatted = _blame?.Format(line, now);
		return _formatted;
	}

	private void Request(string root, string path, TextView view, int delay) {
		int version = view.Buffer.Version;
		_requestedVersion = version;
		_timer?.Dispose();
		TextSnapshot snapshot = view.Snapshot;

		_timer = new Timer(_ => Task.Run(() => {
			GitBlame? blame = GitBlame.Run(root, path, snapshot.GetText());

			Post(() => {
				if (!string.Equals(path, _blamePath, StringComparison.OrdinalIgnoreCase) || version != _requestedVersion) return;

				_blame = blame;
				_blameVersion = version;
				_formattedLine = -1;
				Changed?.Invoke();
			});
		}), null, delay, Timeout.Infinite);
	}
}
