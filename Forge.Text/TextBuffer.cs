namespace Forge.Text;

public readonly record struct TextChange(
	int Offset, int RemovedStart, int RemovedLength, int InsertedStart, int InsertedLength)
{
	public int Delta => InsertedLength - RemovedLength;

	public TextChange Reversed =>
		new(Offset, InsertedStart, InsertedLength, RemovedStart, RemovedLength);
}

public sealed class TextBuffer
{
	private readonly PieceTree _tree;

	private readonly record struct UndoStep(int Start, int Count, int ArenaStart);

	private readonly List<TextChange> _changes = [];
	private readonly List<UndoStep> _undo = [];
	private readonly List<UndoStep> _redo = [];

	private char[] _arena = new char[512];
	private int _arenaLength;

	private EditKind _lastKind = EditKind.Other;

	private TextSnapshot? _cachedSnapshot;
	private int _version;

	private int _groupDepth;
	private int _groupStart;
	private int _groupArenaStart;

	public TextBuffer()
		: this([], 0, LineEnding.CrLf) {
	}

	public TextBuffer(string text)
		: this(text.ToCharArray(), text.Length, DetectLineEnding(text)) {
	}

	internal TextBuffer(char[] content, int length, LineEnding lineEnding) {
		_tree = new PieceTree(content, length);
		LineEnding = lineEnding;
	}

	internal TextBuffer(char[][] chunks, int[] lengths, LineEnding lineEnding) {
		_tree = new PieceTree(chunks, lengths);
		LineEnding = lineEnding;
	}

	public int Version => _version;

	public int Length => _tree.Length;

	public int LineCount => _tree.LineCount;

	public LineEnding LineEnding { get; set; }

	public bool CanUndo => _undo.Count > 0;

	public bool CanRedo => _redo.Count > 0;

	public event Action<TextBuffer, TextChange>? Changed;

	#region Reading

	public TextSnapshot CreateSnapshot() {
		if (_cachedSnapshot is { } cached && cached.Version == _version) return cached;

		TextSnapshot snapshot = new(_tree.SnapshotPieces(), _tree.CreateChunkViews(), _version, LineEnding);
		_cachedSnapshot = snapshot;
		return snapshot;
	}

	public char this[int offset] => _tree.CharAt(offset);

	public string GetText() => _tree.GetText(0, _tree.Length);

	public string GetText(int offset, int count) => _tree.GetText(offset, count);

	public string GetText(TextSpan span) => _tree.GetText(span.Start, span.Length);

	public void CopyTo(int offset, int count, Span<char> destination) => _tree.CopyTo(offset, count, destination);

	public int GetLineStartOffset(int line) => _tree.GetLineStartOffset(line);

	public int GetLineLength(int line) => _tree.GetLineLength(line);

	public int GetLineAt(int offset) => _tree.GetLineAt(offset);

	public TextPosition GetPosition(int offset) {
		int line = _tree.GetLineAt(offset);
		return new TextPosition(line, offset - _tree.GetLineStartOffset(line));
	}

	public int GetOffset(TextPosition position) {
		if (position.Line >= LineCount) return Length;

		int start = _tree.GetLineStartOffset(position.Line);
		return start + Math.Clamp(position.Column, 0, _tree.GetLineLength(position.Line));
	}

	#endregion

	#region Editing

	public void Insert(int offset, ReadOnlySpan<char> text) {
		if (text.IsEmpty) return;

		offset = Math.Clamp(offset, 0, Length);

		DropRedo();
		(int insertedStart, int insertedLength) = Store(text);
		TextChange change = new(offset, 0, 0, insertedStart, insertedLength);

		_tree.Insert(offset, text);
		AfterEdit(change);
	}

	public void Delete(int offset, int count) {
		if (count <= 0) return;

		offset = Math.Clamp(offset, 0, Length);
		count = Math.Min(count, Length - offset);
		if (count <= 0) return;

		DropRedo();
		(int removedStart, int removedLength) = Store(_tree, offset, count);
		TextChange change = new(offset, removedStart, removedLength, 0, 0);

		_tree.Delete(offset, count);
		AfterEdit(change);
	}

	public void Replace(int offset, int count, ReadOnlySpan<char> text) {
		offset = Math.Clamp(offset, 0, Length);
		count = Math.Clamp(count, 0, Length - offset);
		if (count == 0 && text.IsEmpty) return;

		DropRedo();
		(int removedStart, int removedLength) = Store(_tree, offset, count);
		(int insertedStart, int insertedLength) = Store(text);
		TextChange change = new(offset, removedStart, removedLength, insertedStart, insertedLength);

		if (count > 0) _tree.Delete(offset, count);

		if (!text.IsEmpty) _tree.Insert(offset, text);

		AfterEdit(change);
	}

	public void InsertNewLine(int offset) => Insert(offset, LineEnding.ToSpan());

	private void AfterEdit(TextChange change) {
		_version++;

		int arenaStart = Math.Min(
			change.RemovedLength > 0 ? change.RemovedStart : int.MaxValue,
			change.InsertedLength > 0 ? change.InsertedStart : int.MaxValue);

		_changes.Add(change);

		if (_groupDepth == 0) {
			RecordStep(_changes.Count - 1, 1, arenaStart == int.MaxValue ? _arenaLength : arenaStart);
		}

		Changed?.Invoke(this, change);
	}

	private void RecordStep(int start, int count, int arenaStart) {
		if (!ShouldPushStackElementBetween(_lastKind, _kind) && _undo.Count > 0) {
			UndoStep last = _undo[^1];
			_undo[^1] = last with { Count = last.Count + count };
		}
		else _undo.Add(new UndoStep(start, count, arenaStart));

		_lastKind = _kind;
		_kind = EditKind.Other;
	}

	#region Undo arena

	private (int Start, int Length) Store(ReadOnlySpan<char> text) {
		if (text.IsEmpty) return (0, 0);

		EnsureArena(text.Length);

		int start = _arenaLength;
		text.CopyTo(_arena.AsSpan(start));
		_arenaLength += text.Length;

		return (start, text.Length);
	}

	private (int Start, int Length) Store(PieceTree tree, int offset, int count) {
		if (count <= 0) return (0, 0);

		EnsureArena(count);

		int start = _arenaLength;
		tree.CopyTo(offset, count, _arena.AsSpan(start, count));
		_arenaLength += count;

		return (start, count);
	}

	private void EnsureArena(int extra) {
		if (_arenaLength + extra <= _arena.Length) return;

		int capacity = _arena.Length;
		while (capacity < _arenaLength + extra) capacity *= 2;

		Array.Resize(ref _arena, capacity);
	}

	private void DropRedo() {
		if (_redo.Count == 0) return;

		UndoStep earliest = _redo[^1];

		_changes.RemoveRange(earliest.Start, _changes.Count - earliest.Start);
		_arenaLength = earliest.ArenaStart;
		_redo.Clear();
	}

	public ReadOnlySpan<char> RemovedText(in TextChange change) =>
		_arena.AsSpan(change.RemovedStart, change.RemovedLength);

	public ReadOnlySpan<char> InsertedText(in TextChange change) =>
		_arena.AsSpan(change.InsertedStart, change.InsertedLength);

	public int UndoArenaLength => _arenaLength;

	#endregion

	#region Undo grouping

	public enum EditKind
	{
		Other = 0,

		DeletingLeft = 2,
		DeletingRight = 3,
		TypingOther = 4,
		TypingFirstSpace = 5,
		TypingConsecutiveSpace = 6,
	}

	private EditKind _kind = EditKind.Other;

	public void SetEditKind(EditKind kind) => _kind = kind;

	public EditKind TypingKindFor(char character) => character != ' '
		? EditKind.TypingOther
		: _lastKind is EditKind.TypingFirstSpace or EditKind.TypingConsecutiveSpace
			? EditKind.TypingConsecutiveSpace
			: EditKind.TypingFirstSpace;

	private static bool ShouldPushStackElementBetween(EditKind previous, EditKind current) {
		if (current == EditKind.Other) return true;

		if (IsTyping(previous) && !IsTyping(current)) return true;

		if (previous == EditKind.TypingFirstSpace) return false;

		return Normalize(previous) != Normalize(current);
	}

	private static bool IsTyping(EditKind kind) => kind is EditKind.TypingOther or
		EditKind.TypingFirstSpace or EditKind.TypingConsecutiveSpace;

	private static EditKind Normalize(EditKind kind) =>
		kind is EditKind.TypingConsecutiveSpace ? EditKind.TypingFirstSpace : kind;

	#endregion

	#endregion

	#region Undo

	public void BeginEdit() {
		if (_groupDepth++ == 0) {
			_groupStart = _changes.Count;
			_groupArenaStart = _arenaLength;
		}
	}

	public void EndEdit() {
		if (_groupDepth == 0 || --_groupDepth > 0) return;

		int count = _changes.Count - _groupStart;

		if (count == 0) return;

		RecordStep(_groupStart, count, _groupArenaStart);
	}

	public bool Undo(out int caret) {
		caret = -1;

		if (_undo.Count == 0) return false;

		UndoStep step = _undo[^1];
		_undo.RemoveAt(_undo.Count - 1);

		_lastKind = EditKind.Other;

		for (int i = step.Start + step.Count - 1; i >= step.Start; i--) {
			ApplyReverse(_changes[i]);
			caret = _changes[i].Offset + _changes[i].RemovedLength;
		}

		_redo.Add(step);
		return true;
	}

	public bool Redo(out int caret) {
		caret = -1;

		if (_redo.Count == 0) return false;

		UndoStep step = _redo[^1];
		_redo.RemoveAt(_redo.Count - 1);

		_lastKind = EditKind.Other;

		for (int i = step.Start; i < step.Start + step.Count; i++) {
			ApplyForward(_changes[i]);
			caret = _changes[i].Offset + _changes[i].InsertedLength;
		}

		_undo.Add(step);
		return true;
	}

	public bool Undo() => Undo(out _);

	public bool Redo() => Redo(out _);

	private void ApplyReverse(TextChange change) {
		if (change.InsertedLength > 0) _tree.Delete(change.Offset, change.InsertedLength);

		if (change.RemovedLength > 0) _tree.Insert(change.Offset, RemovedText(change));

		_version++;
		Changed?.Invoke(this, change.Reversed);
	}

	private void ApplyForward(TextChange change) {
		if (change.RemovedLength > 0) _tree.Delete(change.Offset, change.RemovedLength);

		if (change.InsertedLength > 0) _tree.Insert(change.Offset, InsertedText(change));

		_version++;
		Changed?.Invoke(this, change);
	}

	#endregion

	internal void Validate() => _tree.Validate();

	internal int PieceCount => _tree.PieceCount;

	private static LineEnding DetectLineEnding(ReadOnlySpan<char> text) {
		int lineFeed = text.IndexOf('\n');
		if (lineFeed < 0) return LineEnding.CrLf;

		return lineFeed > 0 && text[lineFeed - 1] == '\r' ? LineEnding.CrLf : LineEnding.Lf;
	}
}
