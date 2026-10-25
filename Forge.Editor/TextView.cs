using Forge.Common.Editor;
using Forge.Common.Git;
using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Core;
using Forge.Syntax;
using Forge.Text;
using Forge.Theming;

namespace Forge.Editor;

public sealed class TextView : ITextView
{
	public TextView(TextBuffer buffer, string? path = null) {
		Path = path;
		Highlighter = new Highlighter(Languages.ForPath(path));
		Buffer = buffer;
		Snapshot = buffer.CreateSnapshot();
		buffer.CursorState = CurrentSelection;
		buffer.Changed += OnBufferChanged;
	}

	public readonly record struct ViewZone(int Id, int AfterLine, float Height);

	private readonly List<ViewZone> _zones = [];
	private int _zoneCounter;

	public IReadOnlyList<ViewZone> Zones => _zones;

	public int AddZone(int afterLine, float height) {
		ViewZone zone = new(++_zoneCounter, afterLine, height);
		int index = 0;

		while (index < _zones.Count && _zones[index].AfterLine <= afterLine) index++;

		_zones.Insert(index, zone);
		return zone.Id;
	}

	public void ResizeZone(int id, float height) {
		int index = _zones.FindIndex(zone => zone.Id == id);

		if (index >= 0) _zones[index] = _zones[index] with { Height = height };
	}

	public void RemoveZone(int id) => _zones.RemoveAll(zone => zone.Id == id);

	private float ZonesBefore(int line) {
		float total = 0f;

		foreach (ViewZone zone in _zones) {
			if (zone.AfterLine >= line) break;

			total += zone.Height;
		}

		return total;
	}

	public float TopOf(int line, float lineHeight) => (line * lineHeight) + ZonesBefore(line);

	public float ZoneTop(int id, float lineHeight) {
		float above = 0f;

		foreach (ViewZone zone in _zones) {
			if (zone.Id == id) return ((zone.AfterLine + 1) * lineHeight) + above;

			above += zone.Height;
		}

		return 0f;
	}

	public float ContentHeight(float lineHeight) {
		float total = Snapshot.LineCount * lineHeight;

		foreach (ViewZone zone in _zones) total += zone.Height;

		return total;
	}

	public int LineAtY(float contentY, float lineHeight) {
		float consumed = 0f;

		foreach (ViewZone zone in _zones) {
			float zoneTop = ((zone.AfterLine + 1) * lineHeight) + consumed;

			if (contentY < zoneTop) break;

			if (contentY < zoneTop + zone.Height) return Math.Clamp(zone.AfterLine, 0, Math.Max(0, Snapshot.LineCount - 1));

			consumed += zone.Height;
		}

		return Math.Clamp((int)((contentY - consumed) / lineHeight), 0, Math.Max(0, Snapshot.LineCount - 1));
	}

	private void OnBufferChanged(TextBuffer buffer, TextChange change) {
		if (_zones.Count == 0) return;

		int removed = buffer.RemovedText(change).Count('\n');
		int inserted = buffer.InsertedText(change).Count('\n');

		if (removed == inserted) return;

		int line = Snapshot.GetLineAt(Math.Min(change.Offset, Snapshot.Length));

		for (int i = 0; i < _zones.Count; i++) {
			if (_zones[i].AfterLine < line) continue;

			_zones[i] = _zones[i] with { AfterLine = Math.Max(line, _zones[i].AfterLine - removed + inserted) };
		}
	}

	private TextBuffer.Selection CurrentSelection() => new(SelectionAnchor, CaretOffset);

	public void PushUndoStop() => Buffer.PushStackElement();

	public TextSnapshot GetSnapshot() => Snapshot;

	public int GetCaretOffset() => CaretOffset;

	public int GetCaretLine() => CaretLine;

	public int GetCaretColumn() => CaretColumn;

	public int GetSelectionLength() => HasSelection ? Selection.Length : 0;

	public int GetTabWidth() => TabWidth;

	public LineEnding GetLineEnding() => Buffer.LineEnding;

	public string GetLanguageName() => Highlighter.Language.Name;

	(float X, float Y) ITextView.PointForOffset(ITextFont font, int offset, float viewportX, float viewportY) =>
		PointForOffset((ITextFont)font, offset, viewportX, viewportY);

	private const float ChangeMarkWidth = 2f;

	public const float GlyphMarginWidth = 20f;

	private const int GutterPaddingLeft = 12;
	private const int GutterPaddingRight = 16;
	private const int TextPaddingLeft = 8;
	private const int LinesPerWheelNotch = 3;
	private const int ColumnsPerWheelNotch = 6;
	private const float CaretWidth = 2f;
	private const float ScrollbarWidth = 10f;
	private const float ScrollbarMinThumb = 28f;

	private char[] _raw = new char[512];
	private char[] _expanded = new char[512];

	private int[] _columns = new int[513];

	private int _desiredColumn = -1;

	private float _contentWidth;

	private float _textAreaWidth;

	public float LastLineHeight { get; private set; } = 1f;

	public float LastViewportHeight { get; private set; }

	public string? Path { get; }

	public Highlighter Highlighter { get; }

	public TextBuffer Buffer { get; private set; }

	public TextSnapshot Snapshot { get; private set; }

	public float ScrollY { get; private set; }

	public float ScrollX { get; private set; }

	public int TabWidth { get; set; } = 4;

	public bool CaretVisible { get; set; } = true;

	public IFileDiff? Diff { get; set; }

	public void ResetBlink() => CaretVisible = true;

	public int CaretOffset { get; private set; }

	public int SelectionAnchor { get; private set; }

	public bool HasSelection => CaretOffset != SelectionAnchor;

	public TextSpan Selection => TextSpan.FromBounds(
		Math.Min(CaretOffset, SelectionAnchor),
		Math.Max(CaretOffset, SelectionAnchor));

	public int CaretLine => Snapshot.GetLineAt(CaretOffset);

	public int CaretColumn => CaretOffset - Snapshot.GetLineStartOffset(CaretLine);

	public void SetBuffer(TextBuffer buffer) {
		Buffer.Changed -= OnBufferChanged;
		Buffer = buffer;
		buffer.CursorState = CurrentSelection;
		buffer.Changed += OnBufferChanged;
		_zones.Clear();
		Snapshot = buffer.CreateSnapshot();
		ScrollY = 0;
		ScrollX = 0;
		CaretOffset = 0;
		SelectionAnchor = 0;
		_desiredColumn = -1;
		_contentWidth = 0f;
	}

	private void SyncSnapshot(int changedOffset) {
		Snapshot = Buffer.CreateSnapshot();
		Highlighter.InvalidateFrom(Snapshot.GetLineAt(Math.Clamp(changedOffset, 0, Snapshot.Length)));
	}

	public void ExternalEdit(int changedOffset, int delta) {
		SyncSnapshot(changedOffset);

		if (delta != 0 && changedOffset <= CaretOffset) {
			CaretOffset += delta;
			SelectionAnchor += delta;
		}

		CaretOffset = Math.Clamp(CaretOffset, 0, Snapshot.Length);
		SelectionAnchor = Math.Clamp(SelectionAnchor, 0, Snapshot.Length);
		_desiredColumn = -1;
	}

	#region Caret movement

	public void MoveTo(int offset, bool extend) {
		CaretOffset = Math.Clamp(offset, 0, Snapshot.Length);
		if (!extend) SelectionAnchor = CaretOffset;

		_desiredColumn = -1;
		ResetBlink();
	}

	public void MoveHorizontal(int delta, bool extend) {
		if (!extend && HasSelection) {
			TextSpan selection = Selection;
			MoveTo(delta < 0 ? selection.Start : selection.End, extend: false);
			return;
		}

		int target = CaretOffset + delta;

		if (delta > 0 && target < Snapshot.Length && Snapshot[target] == '\n' && Snapshot[target - 1] == '\r') {
			target++;
		}
		else if (delta < 0 && target > 0 && Snapshot[target] == '\n' && Snapshot[target - 1] == '\r') target--;

		MoveTo(target, extend);
	}

	public void MoveVertical(int deltaLines, bool extend) {
		int line = CaretLine;
		int column = _desiredColumn >= 0 ? _desiredColumn : VisualColumnAt(line, CaretOffset);

		int targetLine = Math.Clamp(line + deltaLines, 0, Snapshot.LineCount - 1);
		int offset = OffsetForVisualColumn(targetLine, column);

		CaretOffset = Math.Clamp(offset, 0, Snapshot.Length);
		if (!extend) SelectionAnchor = CaretOffset;

		_desiredColumn = column;
	}

	public void MoveToLineStart(bool extend) {
		int line = CaretLine;
		int start = Snapshot.GetLineStartOffset(line);

		int firstText = start;
		TextSpan span = Snapshot.GetLineSpan(line);
		while (firstText < span.End && (Snapshot[firstText] == ' ' || Snapshot[firstText] == '\t')) firstText++;

		MoveTo(CaretOffset == firstText ? start : firstText, extend);
	}

	public void MoveToLineEnd(bool extend) => MoveTo(Snapshot.GetLineSpan(CaretLine).End, extend);

	public void MoveToDocumentStart(bool extend) => MoveTo(0, extend);

	public void MoveToDocumentEnd(bool extend) => MoveTo(Snapshot.Length, extend);

	public void MoveWord(int direction, bool extend) {
		int offset = CaretOffset;

		if (direction > 0) {
			while (offset < Snapshot.Length && !IsWordCharacter(Snapshot[offset])) offset++;

			while (offset < Snapshot.Length && IsWordCharacter(Snapshot[offset])) offset++;
		}
		else {
			while (offset > 0 && !IsWordCharacter(Snapshot[offset - 1])) offset--;

			while (offset > 0 && IsWordCharacter(Snapshot[offset - 1])) offset--;
		}

		MoveTo(offset, extend);
	}

	public void SelectWordAt(int offset) {
		offset = Math.Clamp(offset, 0, Snapshot.Length);

		TextSpan line = Snapshot.GetLineSpan(Snapshot.GetLineAt(offset));

		int at = offset;

		if (at >= line.End || (at > line.Start && !IsWordCharacter(Snapshot[at]))) at--;

		if (at < line.Start || at >= line.End) {
			MoveTo(offset, extend: false);
			return;
		}

		int start;
		int end;

		if (IsWordCharacter(Snapshot[at])) {
			start = at;
			end = at + 1;

			while (start > line.Start && IsWordCharacter(Snapshot[start - 1])) start--;

			while (end < line.End && IsWordCharacter(Snapshot[end])) end++;
		}
		else if (Snapshot[at] is ' ' or '\t') {
			start = at;
			end = at + 1;

			while (start > line.Start && Snapshot[start - 1] is ' ' or '\t') start--;

			while (end < line.End && Snapshot[end] is ' ' or '\t') end++;
		}
		else {
			start = at;
			end = at + 1;
		}

		SelectionAnchor = start;
		CaretOffset = end;
		_desiredColumn = -1;
	}

	public void SelectLineAt(int offset) {
		int line = Snapshot.GetLineAt(Math.Clamp(offset, 0, Snapshot.Length));

		SelectionAnchor = Snapshot.GetLineStartOffset(line);
		CaretOffset = line + 1 < Snapshot.LineCount
			? Snapshot.GetLineStartOffset(line + 1)
			: Snapshot.GetLineSpan(line).End;

		_desiredColumn = -1;
	}

	public void SelectAll() {
		SelectionAnchor = 0;
		CaretOffset = Snapshot.Length;
		_desiredColumn = -1;
	}

	private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

	#endregion

	#region Editing

	public void InsertText(ReadOnlySpan<char> text) {
		if (text.IsEmpty) return;

		_autoClosed.Clear();

		Buffer.SetEditKind(text.Length == 1 && !HasSelection
			? Buffer.TypingKindFor(text[0])
			: TextBuffer.EditKind.Other);

		Buffer.BeginEdit();
		int at = DeleteSelectionCore();
		Buffer.Insert(at, text);
		Buffer.EndEdit();

		SyncSnapshot(at);
		MoveTo(at + text.Length, extend: false);
	}

	public void InsertNewLine() {
		if (HasSelection) DeleteSelection();

		TextSpan span = Snapshot.GetLineSpan(CaretLine);

		string before = Buffer.GetText(span.Start, CaretOffset - span.Start);
		string after = Buffer.GetText(CaretOffset, span.End - CaretOffset);
		string newline = Buffer.LineEnding.ToSpan().ToString();

		string indent = before[..(before.Length - before.TrimStart(' ', '\t').Length)];
		string trimmed = before.TrimEnd();

		if (trimmed.TrimStart().StartsWith("///", StringComparison.Ordinal)) {
			InsertText(newline + indent + "/// ");
			return;
		}

		if ((trimmed.TrimStart().StartsWith("//", StringComparison.Ordinal) ||
				trimmed.Contains(" // ", StringComparison.Ordinal)) &&
			after.Trim().Length > 0) {
			InsertText(newline + indent + "// ");
			return;
		}

		bool opens = trimmed.Length > 0 && trimmed[^1] is '{' or '[' or '(';
		bool closesNext = after.TrimStart() is { Length: > 0 } rest && rest[0] is '}' or ']' or ')';

		if (opens && closesNext) {
			string inner = indent + IndentUnit(indent);

			InsertText(newline + inner + newline + indent);
			MoveTo(CaretOffset - newline.Length - indent.Length, extend: false);
			return;
		}

		InsertText(newline + indent + (opens ? IndentUnit(indent) : string.Empty));
	}

	private string IndentUnit(string indent) =>
		indent.Length > 0 && indent[^1] == ' ' ? new string(' ', TabWidth) : "\t";

	public void InsertTab() => InsertText("\t");

	#region Auto-closing pairs

	private static readonly (char Open, char Close)[] AutoClosingPairs =
		[('{', '}'), ('[', ']'), ('(', ')'), ('\'', '\''), ('"', '"')];

	private const string AutoCloseBeforeBrackets = "'\"`;:.,=}])> \n\t";

	private const string AutoCloseBeforeQuotes = ";:.,=}])> \n\t";

	private readonly List<int> _autoClosed = [];

	public bool TryAutoClose(char character) {
		if (HasSelection && Array.FindIndex(AutoClosingPairs, p => p.Open == character) is >= 0 and int surround) {
			TextSpan selection = Selection;
			string text = Buffer.GetText(selection);

			Buffer.SetEditKind(TextBuffer.EditKind.Other);
			Buffer.BeginEdit();
			Buffer.Replace(selection.Start, selection.Length,
				character + text + AutoClosingPairs[surround].Close);

			Buffer.EndEdit();
			SyncSnapshot(selection.Start);

			SelectionAnchor = selection.Start + 1;
			CaretOffset = selection.Start + 1 + text.Length;
			_desiredColumn = -1;

			return true;
		}

		if (HasSelection) return false;

		char afterCaret = CaretOffset < Snapshot.Length ? Snapshot[CaretOffset] : '\0';

		if (afterCaret == character && _autoClosed.Contains(CaretOffset) &&
			Array.Exists(AutoClosingPairs, p => p.Close == character)) {
			bool escaped = character is '\'' or '"' && CaretOffset > 0 && Snapshot[CaretOffset - 1] == '\\';

			if (!escaped) {
				_autoClosed.Remove(CaretOffset);
				MoveTo(CaretOffset + 1, extend: false);
				return true;
			}
		}

		int pair = Array.FindIndex(AutoClosingPairs, p => p.Open == character);

		if (pair < 0) return false;

		char close = AutoClosingPairs[pair].Close;
		bool isQuote = character is '\'' or '"';

		if (afterCaret != '\0') {
			string allowed = isQuote ? AutoCloseBeforeQuotes : AutoCloseBeforeBrackets;

			bool beforeClosingBrace = Array.Exists(AutoClosingPairs, p => p.Close == afterCaret) &&
				!Array.Exists(AutoClosingPairs, p => p.Open == afterCaret);

			if (!beforeClosingBrace && allowed.IndexOf(afterCaret) < 0) return false;
		}

		if (isQuote && CaretOffset > 0 && IsWordCharacter(Snapshot[CaretOffset - 1])) return false;

		int at = CaretOffset;

		Buffer.SetEditKind(TextBuffer.EditKind.Other);
		Buffer.BeginEdit();
		Buffer.Insert(at, [character, close]);
		Buffer.EndEdit();

		SyncSnapshot(at);
		MoveTo(at + 1, extend: false);

		_autoClosed.Add(at + 1);

		return true;
	}

	public void ForgetAutoClosed() => _autoClosed.Clear();

	#endregion

	public void Backspace() {
		if (HasSelection) {
			DeleteSelection();
			return;
		}

		if (CaretOffset == 0) return;

		Buffer.SetEditKind(TextBuffer.EditKind.DeletingLeft);

		if (_autoClosed.Contains(CaretOffset) && CaretOffset > 0 && CaretOffset < Snapshot.Length &&
			Array.Exists(AutoClosingPairs,
				p => p.Open == Snapshot[CaretOffset - 1] && p.Close == Snapshot[CaretOffset])) {
			int pairStart = CaretOffset - 1;

			_autoClosed.Clear();

			Buffer.BeginEdit();
			Buffer.Delete(pairStart, 2);
			Buffer.EndEdit();

			SyncSnapshot(pairStart);
			MoveTo(pairStart, extend: false);
			return;
		}

		_autoClosed.Clear();

		int start = CaretOffset - 1;
		if (start > 0 && Snapshot[start] == '\n' && Snapshot[start - 1] == '\r') start--;

		Buffer.Delete(start, CaretOffset - start);
		SyncSnapshot(start);
		MoveTo(start, extend: false);
	}

	public void DeleteForward() {
		if (HasSelection) {
			DeleteSelection();
			return;
		}

		if (CaretOffset >= Snapshot.Length) return;

		Buffer.SetEditKind(TextBuffer.EditKind.DeletingRight);
		_autoClosed.Clear();

		int end = CaretOffset + 1;
		if (Snapshot[CaretOffset] == '\r' && end < Snapshot.Length && Snapshot[end] == '\n') end++;

		Buffer.Delete(CaretOffset, end - CaretOffset);
		SyncSnapshot(CaretOffset);
		MoveTo(CaretOffset, extend: false);
	}

	public void DeleteSelection() {
		if (!HasSelection) return;

		int at = DeleteSelectionCore();
		SyncSnapshot(at);
		MoveTo(at, extend: false);
	}

	private int DeleteSelectionCore() {
		if (!HasSelection) return CaretOffset;

		TextSpan selection = Selection;
		Buffer.Delete(selection.Start, selection.Length);
		return selection.Start;
	}

	public void DeleteWord(int direction) {
		if (HasSelection) {
			DeleteSelection();
			return;
		}

		int offset = CaretOffset;

		if (direction > 0) {
			while (offset < Snapshot.Length && !IsWordCharacter(Snapshot[offset]) &&
				Snapshot[offset] is not ('\n' or '\r')) offset++;

			while (offset < Snapshot.Length && IsWordCharacter(Snapshot[offset])) offset++;

			if (offset == CaretOffset && offset < Snapshot.Length) {
				DeleteForward();
				return;
			}

			Buffer.Delete(CaretOffset, offset - CaretOffset);
			SyncSnapshot(CaretOffset);
			MoveTo(CaretOffset, extend: false);
			return;
		}

		while (offset > 0 && !IsWordCharacter(Snapshot[offset - 1]) &&
			Snapshot[offset - 1] is not ('\n' or '\r')) offset--;

		while (offset > 0 && IsWordCharacter(Snapshot[offset - 1])) offset--;

		if (offset == CaretOffset) {
			Backspace();
			return;
		}

		Buffer.Delete(offset, CaretOffset - offset);
		SyncSnapshot(offset);
		MoveTo(offset, extend: false);
	}

	public void DeleteLines() {
		(int first, int last) = SelectedLines();

		int start = Snapshot.GetLineStartOffset(first);
		int end = last + 1 < Snapshot.LineCount
			? Snapshot.GetLineStartOffset(last + 1)
			: Snapshot.GetLineSpan(last).End;

		if (end == start && start > 0) start = Snapshot.GetLineSpan(first - 1).End;

		Buffer.BeginEdit();
		Buffer.Delete(start, end - start);
		Buffer.EndEdit();

		SyncSnapshot(start);
		MoveTo(Math.Min(start, Snapshot.Length), extend: false);
	}

	public void MoveLines(int direction) {
		(int first, int last) = SelectedLines();

		int target = direction < 0 ? first - 1 : last + 1;

		if (target < 0 || target >= Snapshot.LineCount) return;

		int blockStart = Snapshot.GetLineStartOffset(Math.Min(first, target));
		int blockEnd = Snapshot.GetLineSpan(Math.Max(last, target)).End;

		string moving = Buffer.GetText(Snapshot.GetLineStartOffset(first),
			Snapshot.GetLineSpan(last).End - Snapshot.GetLineStartOffset(first));

		string other = Buffer.GetText(Snapshot.GetLineStartOffset(target),
			Snapshot.GetLineSpan(target).End - Snapshot.GetLineStartOffset(target));

		string separator = Buffer.LineEnding.ToSpan().ToString();

		string replacement = direction < 0
			? moving + separator + other
			: other + separator + moving;

		int caretOffsetInLine = CaretOffset - Snapshot.GetLineStartOffset(CaretLine);
		int caretLineWithinBlock = CaretLine - first;

		Buffer.BeginEdit();
		Buffer.Replace(blockStart, blockEnd - blockStart, replacement);
		Buffer.EndEdit();

		SyncSnapshot(blockStart);

		int movedFirst = direction < 0 ? first - 1 : first + 1;
		int caretLine = Math.Clamp(movedFirst + caretLineWithinBlock, 0, Snapshot.LineCount - 1);
		TextSpan caretSpan = Snapshot.GetLineSpan(caretLine);

		int anchorLine = Math.Clamp(movedFirst, 0, Snapshot.LineCount - 1);
		int movedLast = Math.Clamp(movedFirst + (last - first), 0, Snapshot.LineCount - 1);

		if (first != last) {
			SelectionAnchor = Snapshot.GetLineStartOffset(anchorLine);
			CaretOffset = Snapshot.GetLineSpan(movedLast).End;
			_desiredColumn = -1;
			return;
		}

		MoveTo(Math.Min(caretSpan.Start + caretOffsetInLine, caretSpan.End), extend: false);
	}

	public void CopyLines(int direction) {
		(int first, int last) = SelectedLines();

		int start = Snapshot.GetLineStartOffset(first);
		int end = Snapshot.GetLineSpan(last).End;

		string text = Buffer.GetText(start, end - start);
		string separator = Buffer.LineEnding.ToSpan().ToString();

		Buffer.BeginEdit();
		Buffer.Insert(direction < 0 ? start : end, direction < 0 ? text + separator : separator + text);
		Buffer.EndEdit();

		SyncSnapshot(start);

		int lines = last - first + 1;
		int caretLine = Math.Clamp(CaretLine + (direction < 0 ? 0 : lines), 0, Snapshot.LineCount - 1);

		TextSpan span = Snapshot.GetLineSpan(caretLine);
		MoveTo(Math.Min(span.Start + CaretColumn, span.End), extend: false);
	}

	public void IndentLines(int direction) {
		(int first, int last) = SelectedLines();

		int start = Snapshot.GetLineStartOffset(first);
		int end = Snapshot.GetLineSpan(last).End;

		string text = Buffer.GetText(start, end - start);
		string separator = Buffer.LineEnding.ToSpan().ToString();

		string[] lines = text.Split('\n');
		System.Text.StringBuilder builder = new(text.Length + lines.Length);

		for (int i = 0; i < lines.Length; i++) {
			string line = lines[i].TrimEnd('\r');

			if (direction > 0) builder.Append(line.Length == 0 ? line : "\t" + line);
			else if (line.StartsWith('\t')) builder.Append(line[1..]);
			else {
				int spaces = 0;

				while (spaces < line.Length && spaces < TabWidth && line[spaces] == ' ') spaces++;

				builder.Append(line[spaces..]);
			}

			if (i < lines.Length - 1) builder.Append(separator);
		}

		Buffer.BeginEdit();
		Buffer.Replace(start, end - start, builder.ToString());
		Buffer.EndEdit();

		SyncSnapshot(start);

		SelectionAnchor = Snapshot.GetLineStartOffset(Math.Clamp(first, 0, Snapshot.LineCount - 1));
		CaretOffset = Snapshot.GetLineSpan(Math.Clamp(last, 0, Snapshot.LineCount - 1)).End;
		_desiredColumn = -1;
	}

	public void ToggleLineComment(string marker = "//") {
		(int first, int last) = SelectedLines();

		int start = Snapshot.GetLineStartOffset(first);
		int end = Snapshot.GetLineSpan(last).End;

		string text = Buffer.GetText(start, end - start);
		string separator = Buffer.LineEnding.ToSpan().ToString();
		string[] lines = text.Split('\n');

		bool allCommented = true;
		int indent = int.MaxValue;

		foreach (string raw in lines) {
			string line = raw.TrimEnd('\r');

			if (line.Trim().Length == 0) continue;

			if (!line.TrimStart().StartsWith(marker, StringComparison.Ordinal)) allCommented = false;

			indent = Math.Min(indent, line.Length - line.TrimStart().Length);
		}

		if (indent == int.MaxValue) indent = 0;

		System.Text.StringBuilder builder = new(text.Length + (lines.Length * 3));

		for (int i = 0; i < lines.Length; i++) {
			string line = lines[i].TrimEnd('\r');

			if (line.Trim().Length == 0) builder.Append(line);
			else if (allCommented) {
				int at = line.IndexOf(marker, StringComparison.Ordinal);

				int after = at + marker.Length;

				if (after < line.Length && line[after] == ' ') after++;

				builder.Append(line[..at]).Append(line[after..]);
			}
			else {
				builder.Append(line[..Math.Min(indent, line.Length)])
					.Append(marker)
					.Append(' ')
					.Append(line[Math.Min(indent, line.Length)..]);
			}

			if (i < lines.Length - 1) builder.Append(separator);
		}

		Buffer.BeginEdit();
		Buffer.Replace(start, end - start, builder.ToString());
		Buffer.EndEdit();

		SyncSnapshot(start);

		if (first != last) {
			SelectionAnchor = Snapshot.GetLineStartOffset(first);
			CaretOffset = Snapshot.GetLineSpan(Math.Clamp(last, 0, Snapshot.LineCount - 1)).End;
			_desiredColumn = -1;
			return;
		}

		TextSpan span = Snapshot.GetLineSpan(Math.Clamp(first, 0, Snapshot.LineCount - 1));
		MoveTo(Math.Min(span.Start + CaretColumn, span.End), extend: false);
	}

	public void InsertLine(int direction) {
		int line = CaretLine;
		TextSpan span = Snapshot.GetLineSpan(line);

		string current = Buffer.GetText(span.Start, span.Length);
		string indent = current[..(current.Length - current.TrimStart(' ', '\t').Length)];
		string separator = Buffer.LineEnding.ToSpan().ToString();

		int at = direction < 0 ? span.Start : span.End;
		string inserted = direction < 0 ? indent + separator : separator + indent;

		Buffer.BeginEdit();
		Buffer.Insert(at, inserted);
		Buffer.EndEdit();

		SyncSnapshot(at);
		MoveTo(direction < 0 ? at + indent.Length : at + inserted.Length, extend: false);
	}

	public void SelectLine() {
		(int first, int last) = SelectedLines();

		int end = last + 1 < Snapshot.LineCount
			? Snapshot.GetLineStartOffset(last + 1)
			: Snapshot.GetLineSpan(last).End;

		SelectionAnchor = Snapshot.GetLineStartOffset(first);
		CaretOffset = end;
		_desiredColumn = -1;
	}

	private (int First, int Last) SelectedLines() {
		if (!HasSelection) return (CaretLine, CaretLine);

		TextSpan selection = Selection;
		int first = Snapshot.GetLineAt(selection.Start);
		int last = Snapshot.GetLineAt(selection.End);

		if (last > first && selection.End == Snapshot.GetLineStartOffset(last)) last--;

		return (first, last);
	}

	public string GetSelectedText() => HasSelection ? Buffer.GetText(Selection) : string.Empty;

	public string CopyText() {
		if (HasSelection) return Buffer.GetText(Selection);

		TextSpan span = Snapshot.GetLineSpan(CaretLine);
		return Buffer.GetText(span.Start, span.Length) + Buffer.LineEnding.ToSpan().ToString();
	}

	public void Cut() {
		if (HasSelection) {
			DeleteSelection();
			return;
		}

		DeleteLines();
	}

	public void SelectNextOccurrence() {
		if (!HasSelection) {
			SelectWordAt(CaretOffset);
			return;
		}

		string word = GetSelectedText();

		if (word.Length == 0 || word.Contains('\n')) return;

		string text = Buffer.GetText(0, Snapshot.Length);
		int from = Math.Max(Selection.Start + 1, 0);

		int found = text.IndexOf(word, Math.Min(from, text.Length), StringComparison.Ordinal);

		if (found < 0) found = text.IndexOf(word, StringComparison.Ordinal);

		if (found < 0) return;

		SelectionAnchor = found;
		CaretOffset = found + word.Length;
		_desiredColumn = -1;
	}

	public void GoToMatchingBracket() {
		const string open = "([{";
		const string close = ")]}";

		int at = CaretOffset;

		if (at >= Snapshot.Length || (open.IndexOf(Snapshot[at]) < 0 && close.IndexOf(Snapshot[at]) < 0)) at--;

		if (at < 0 || at >= Snapshot.Length) return;

		char bracket = Snapshot[at];
		int opening = open.IndexOf(bracket);
		int closing = close.IndexOf(bracket);

		if (opening >= 0) {
			char wanted = close[opening];
			int depth = 0;

			for (int i = at; i < Snapshot.Length; i++) {
				if (Snapshot[i] == bracket) depth++;
				else if (Snapshot[i] == wanted && --depth == 0) {
					MoveTo(i, extend: false);
					return;
				}
			}
		}
		else if (closing >= 0) {
			char wanted = open[closing];
			int depth = 0;

			for (int i = at; i >= 0; i--) {
				if (Snapshot[i] == bracket) depth++;
				else if (Snapshot[i] == wanted && --depth == 0) {
					MoveTo(i, extend: false);
					return;
				}
			}
		}
	}

	public void Undo() {
		if (!Buffer.Undo(out TextBuffer.Selection restore, out int caret)) return;

		Restore(restore, caret);
	}

	public void Redo() {
		if (!Buffer.Redo(out TextBuffer.Selection restore, out int caret)) return;

		Restore(restore, caret);
	}

	private void Restore(TextBuffer.Selection restore, int caret) {
		SyncSnapshot(caret < 0 ? 0 : caret);

		if (restore.IsSet) {
			MoveTo(restore.Anchor, extend: false);
			MoveTo(restore.Caret, extend: true);
			return;
		}

		MoveTo(caret < 0 ? Math.Min(CaretOffset, Snapshot.Length) : caret, extend: false);
	}

	#endregion

	#region Scrolling

	public void ScrollByLines(float lines, float lineHeight, float viewportHeight) {
		ScrollY += lines * lineHeight;
		ClampScroll(lineHeight, viewportHeight);
	}

	public void ScrollByWheel(int wheelDelta, float lineHeight, float viewportHeight) {
		ScrollByLines(-(wheelDelta / 120f) * LinesPerWheelNotch, lineHeight, viewportHeight);
	}

	public void ScrollByColumns(float columns, float cellWidth) {
		ScrollX += columns * cellWidth;
		ClampScrollX();
	}

	public void ScrollByHorizontalWheel(int wheelDelta, float cellWidth) {
		ScrollByColumns((wheelDelta / 120f) * ColumnsPerWheelNotch, cellWidth);
	}

	private void ClampScrollX() {
		float maxScroll = Math.Max(0f, _contentWidth - _textAreaWidth);
		ScrollX = Math.Clamp(ScrollX, 0f, maxScroll);
	}

	private void ClampScroll(float lineHeight, float viewportHeight) {
		float maxScroll = Math.Max(0f, ContentHeight(lineHeight) - viewportHeight);
		ScrollY = Math.Clamp(ScrollY, 0f, maxScroll);
	}

	public void RevealLineInCenterIfOutsideViewport(int line, float lineHeight, float viewportHeight) {
		float top = TopOf(line, lineHeight);

		if (top >= ScrollY && top + lineHeight <= ScrollY + viewportHeight) return;

		ScrollY = top - ((viewportHeight - lineHeight) / 2f);
		ClampScroll(lineHeight, viewportHeight);
	}

	public void EnsureCaretVisible(ITextFont font, float viewportWidth, float viewportHeight) {
		float lineHeight = font.GetLineHeight();
		float caretTop = TopOf(CaretLine, lineHeight);

		if (caretTop < ScrollY) ScrollY = caretTop;
		else if (caretTop + lineHeight > ScrollY + viewportHeight) {
			ScrollY = caretTop + lineHeight - viewportHeight;
		}

		ClampScroll(lineHeight, viewportHeight);

		float textWidth = Math.Max(0f, viewportWidth - GutterWidth(font) - TextPaddingLeft);
		float caretX = VisualColumnAt(CaretLine, CaretOffset) * font.GetCellWidth();

		if (caretX < ScrollX) ScrollX = caretX;
		else if (caretX + CaretWidth > ScrollX + textWidth) ScrollX = caretX + CaretWidth - textWidth;

		ScrollX = Math.Max(0f, ScrollX);
	}

	#endregion

	#region Coordinate mapping

	public float GutterWidth(ITextFont font) {
		int digits = Math.Max(2, DigitCount(Snapshot.LineCount));
		return GlyphMarginWidth + GutterPaddingLeft + (digits * font.GetCellWidth()) + GutterPaddingRight;
	}

	public int OffsetFromPoint(ITextFont font, float x, float y, float viewportX, float viewportY) {
		int line = LineAtY(y - viewportY + ScrollY, font.GetLineHeight());

		float textLeft = viewportX + GutterWidth(font) + TextPaddingLeft - ScrollX;

		int column = Math.Max(0, (int)MathF.Round((x - textLeft) / font.GetCellWidth()));
		return OffsetForVisualColumn(line, column);
	}

	public (float X, float Y) PointForOffset(ITextFont font, int offset, float viewportX,
		float viewportY) {
		int line = Snapshot.GetLineAt(offset);

		return (viewportX + GutterWidth(font) + TextPaddingLeft - ScrollX +
				(VisualColumnAt(line, offset) * font.GetCellWidth()),
			viewportY + TopOf(line, font.GetLineHeight()) - ScrollY);
	}

	public PixelRect CaretRect(ITextFont font, float viewportX, float viewportY, float viewportWidth,
		float viewportHeight) {
		(float caretX, float caretY) = PointForOffset(font, CaretOffset, viewportX, viewportY);

		PixelRect caret = PixelRect.Around(caretX - 1f, caretY - 1f, CaretWidth + 2f, font.GetLineHeight() + 2f);

		return caret.Intersect(PixelRect.Around(viewportX, viewportY, viewportWidth, viewportHeight));
	}

	private bool _rendered;
	private float _renderedX;
	private float _renderedY;
	private float _renderedWidth;
	private float _renderedHeight;
	private float _renderedScrollX;
	private float _renderedScrollY;
	private int _renderedVersion;
	private int _renderedCaret;
	private int _renderedSelectionStart;
	private int _renderedSelectionEnd;

	public PixelRect PendingDamage(ITextFont font, float viewportX, float viewportY,
		float viewportWidth, float viewportHeight) {
		PixelRect viewport = PixelRect.Around(viewportX, viewportY, viewportWidth, viewportHeight);

		if (!_rendered ||
			_renderedX != viewportX || _renderedY != viewportY ||
			_renderedWidth != viewportWidth || _renderedHeight != viewportHeight ||
			_renderedScrollX != ScrollX || _renderedScrollY != ScrollY) return viewport;

		PixelRect damage = PixelRect.Empty;

		int caretLine = CaretLine;
		int previousCaretLine = Snapshot.GetLineAt(Math.Clamp(_renderedCaret, 0, Snapshot.Length));

		if (_renderedVersion != Buffer.Version) {
			int from = Math.Min(caretLine, previousCaretLine);
			damage = damage.Union(RowsFrom(font, from, viewportX, viewportY, viewportHeight));
		}
		else if (_renderedCaret != CaretOffset) {
			damage = damage.Union(RowRect(font, previousCaretLine, viewportX, viewportY, viewportWidth));
			damage = damage.Union(RowRect(font, caretLine, viewportX, viewportY, viewportWidth));
		}

		TextSpan selection = Selection;

		if (_renderedSelectionStart != selection.Start || _renderedSelectionEnd != selection.End) {
			int from = Math.Min(
				Snapshot.GetLineAt(Math.Clamp(Math.Min(_renderedSelectionStart, selection.Start), 0, Snapshot.Length)),
				caretLine);

			int to = Math.Max(
				Snapshot.GetLineAt(Math.Clamp(Math.Max(_renderedSelectionEnd, selection.End), 0, Snapshot.Length)),
				caretLine);

			damage = damage.Union(RowSpanRect(font, from, to, viewportX, viewportY, viewportWidth));
		}

		return damage.Intersect(viewport);
	}

	private void RecordRendered(float x, float y, float width, float height) {
		TextSpan selection = Selection;

		_rendered = true;
		_renderedX = x;
		_renderedY = y;
		_renderedWidth = width;
		_renderedHeight = height;
		_renderedScrollX = ScrollX;
		_renderedScrollY = ScrollY;
		_renderedVersion = Buffer.Version;
		_renderedCaret = CaretOffset;
		_renderedSelectionStart = selection.Start;
		_renderedSelectionEnd = selection.End;
	}

	private PixelRect RowRect(ITextFont font, int line, float viewportX, float viewportY,
		float viewportWidth) =>
		RowSpanRect(font, line, line, viewportX, viewportY, viewportWidth);

	private PixelRect RowSpanRect(ITextFont font, int firstLine, int lastLine, float viewportX,
		float viewportY, float viewportWidth) {
		float top = viewportY + (firstLine * font.GetLineHeight()) - ScrollY;
		float bottom = viewportY + ((lastLine + 1) * font.GetLineHeight()) - ScrollY;

		return PixelRect.Around(viewportX, top, viewportWidth, bottom - top);
	}

	private PixelRect RowsFrom(ITextFont font, int line, float viewportX, float viewportY,
		float viewportHeight) {
		float top = viewportY + (line * font.GetLineHeight()) - ScrollY;

		return PixelRect.Around(viewportX, top, _renderedWidth,
			(viewportY + viewportHeight) - top);
	}

	public int VisualColumnAt(int line, int offset) {
		TextSpan span = Snapshot.GetLineSpan(line);
		int limit = Math.Clamp(offset, span.Start, span.End) - span.Start;
		if (limit <= 0) return 0;

		EnsureCapacity(ref _raw, span.Length);
		Snapshot.CopyTo(span.Start, span.Length, _raw);

		int column = 0;
		for (int i = 0; i < limit; i++) {
			column = _raw[i] == '\t' ? ((column / TabWidth) + 1) * TabWidth : column + 1;
		}

		return column;
	}

	public int OffsetForVisualColumn(int line, int visualColumn) {
		TextSpan span = Snapshot.GetLineSpan(line);
		if (span.Length == 0 || visualColumn <= 0) return span.Start;

		EnsureCapacity(ref _raw, span.Length);
		Snapshot.CopyTo(span.Start, span.Length, _raw);

		int column = 0;
		for (int i = 0; i < span.Length; i++) {
			if (column >= visualColumn) return span.Start + i;

			column = _raw[i] == '\t' ? ((column / TabWidth) + 1) * TabWidth : column + 1;
		}

		return span.End;
	}

	#endregion

	#region Rendering

	public void Render(IPainter painter, float x, float y, float width, float height) {
		ITextFont font = painter.GetCode();
		float lineHeight = font.GetLineHeight();

		painter.FillRectangle(x, y, width, height, Theme.EditorBackground);

		float gutterWidth = GutterWidth(font);
		float textLeft = x + gutterWidth + TextPaddingLeft - ScrollX;

		_textAreaWidth = Math.Max(0f, width - gutterWidth - TextPaddingLeft);
		LastLineHeight = lineHeight;
		LastViewportHeight = height;
		ClampScrollX();

		int firstLine = LineAtY(ScrollY, lineHeight);
		int lastLine = Math.Min(Snapshot.LineCount - 1, LineAtY(ScrollY + height, lineHeight) + 1);

		int caretLine = CaretLine;
		TextSpan selection = Selection;

		painter.PushClip(x, y, width, height);

		for (int line = firstLine; line <= lastLine; line++) {
			float lineTop = y + TopOf(line, lineHeight) - ScrollY;

			if (!painter.NeedsPaint(x, lineTop, width, lineHeight)) continue;

			if (line == caretLine && !HasSelection) {
				painter.FillRectangle(x + gutterWidth, lineTop, width - gutterWidth, lineHeight,
					Theme.CurrentLineBackground);

				painter.FillRectangle(x, lineTop, 2f, lineHeight, Theme.Accent);
			}

			if (HasSelection) DrawSelection(painter, line, selection, textLeft, lineTop, lineHeight);

			DrawDecorations(painter, line, textLeft, lineTop, lineHeight);
			DrawIndentGuides(painter, line, textLeft, lineTop, lineHeight, caretLine);
			DrawChangeMark(painter, line, x, lineTop, lineHeight);
			DrawExecutionLine(painter, line, x, lineTop, width, lineHeight);
			DrawLineNumber(painter, line, caretLine, x, lineTop, gutterWidth);
			DrawGlyphMargin(painter, line, x, lineTop, lineHeight);

			if (Link is { } link && link.Line == line) DrawLinkedLine(painter, link, textLeft, lineTop, lineHeight, x + width);
			else DrawLineText(painter, line, textLeft, lineTop);

			DrawLightbulb(painter, line, x, textLeft, lineTop, lineHeight);
		}

		if (CaretVisible) DrawCaret(painter, caretLine, firstLine, textLeft, y, lineHeight);

		painter.PopClip();

		painter.DrawVerticalLine(x + gutterWidth, y, y + height, Theme.ChromeBorder);

		DrawScrollbar(painter, x, y, width, height, lineHeight);
		DrawHorizontalScrollbar(painter, x, y, width, height, gutterWidth);

		RecordRendered(x, y, width, height);
	}

	public IReadOnlyList<Decoration>? Decorations { get; set; }

	public Func<int, BreakpointMark>? BreakpointProvider { get; set; }

	public int ExecutionLine { get; set; } = -1;

	public int HoverGutterLine { get; set; } = -1;

	public readonly record struct LinkRange(int Line, int StartColumn, int EndColumn);

	public readonly record struct LightbulbMark(int Line, int Column, bool InGutter, bool AutoFix);

	public LightbulbMark? Lightbulb {
		get;
		set {
			if (field == value) return;

			field = value;
			LightbulbBounds = null;
		}
	}

	public (float Left, float Top, float Width, float Height)? LightbulbBounds { get; private set; }

	private void DrawLightbulb(IPainter painter, int line, float glyphX, float textLeft, float lineTop, float lineHeight) {
		if (Lightbulb is not { } mark || mark.Line != line) return;

		float size = MathF.Min(lineHeight, GlyphMarginWidth);
		float left = mark.InGutter ? glyphX : textLeft + (VisualColumnAt(line, Math.Min(Snapshot.GetLineSpan(line).Start + mark.Column, Snapshot.GetLineSpan(line).End)) * painter.GetCode().GetCellWidth());

		LightbulbBounds = (left, lineTop, size, lineHeight);
		painter.DrawIcon(mark.AutoFix ? Codicon.LightbulbAutofix : Codicon.LightBulb, left + (size / 2f), lineTop + (lineHeight / 2f), size, mark.AutoFix ? Theme.EditorLightBulbAutoFixForeground : Theme.EditorLightBulbForeground);
	}

	public LinkRange? Link { get; set; }

	private void DrawLinkedLine(IPainter painter, LinkRange link, float textLeft, float lineTop, float lineHeight, float right) {
		TextSpan span = Snapshot.GetLineSpan(link.Line);
		int start = Math.Min(span.Start + link.StartColumn, span.End);
		int end = Math.Min(span.Start + link.EndColumn, span.End);
		float cellWidth = painter.GetCode().GetCellWidth();
		float linkLeft = textLeft + (VisualColumnAt(link.Line, start) * cellWidth);
		float linkRight = textLeft + (VisualColumnAt(link.Line, end) * cellWidth);

		painter.PushClip(textLeft, lineTop, Math.Max(0f, linkLeft - textLeft), lineHeight);
		DrawLineText(painter, link.Line, textLeft, lineTop);
		painter.PopClip();

		painter.PushClip(linkRight, lineTop, Math.Max(0f, right - linkRight), lineHeight);
		DrawLineText(painter, link.Line, textLeft, lineTop);
		painter.PopClip();

		if (end <= start) return;

		ITextFont font = painter.GetCode();
		painter.DrawText(font, Snapshot.GetText(start, end - start), linkLeft, lineTop + font.GetBaseline(), Theme.EditorLinkActiveForeground);
		painter.DrawHorizontalLine(lineTop + font.GetBaseline() + MathF.Max(1f, MathF.Round(lineHeight * 0.08f)), linkLeft, linkRight, Theme.EditorLinkActiveForeground);
	}

	public bool ExecutionIsTopFrame { get; set; } = true;

	private void DrawGlyphMargin(IPainter painter, int line, float x, float lineTop, float lineHeight) {
		float centreX = x + GlyphMarginWidth * 0.5f;
		float centreY = lineTop + lineHeight * 0.5f;
		float size = MathF.Min(lineHeight, GlyphMarginWidth);

		BreakpointMark mark = BreakpointProvider?.Invoke(line + 1) ?? BreakpointMark.None;
		bool executing = line == ExecutionLine;

		if (mark == BreakpointMark.None) {
			if (executing) {
				painter.DrawIcon(Codicon.DebugStackframe, centreX, centreY, size,
					ExecutionIsTopFrame ? Theme.DebugStackframeForeground : Theme.DebugStackframeFocusedForeground);
				return;
			}

			if (line != HoverGutterLine) return;

			painter.DrawIcon(Codicon.DebugBreakpoint, centreX, centreY, size,
				Theme.DebugBreakpointHover);
			return;
		}

		Codicon icon = mark switch {
			BreakpointMark.Conditional => Codicon.DebugBreakpointConditional,
			BreakpointMark.LogPoint => Codicon.DebugBreakpointLog,
			BreakpointMark.Unverified => Codicon.DebugBreakpointUnverified,
			_ => Codicon.DebugBreakpoint,
		};

		Color color = mark switch {
			BreakpointMark.Disabled or BreakpointMark.Unverified => Theme.DebugBreakpointDisabled,
			BreakpointMark.LogPoint => Theme.DebugBreakpointLogPoint,
			_ => Theme.DebugBreakpointForeground,
		};

		painter.DrawIcon(icon, centreX, centreY, size, color);

		if (executing) {
			painter.DrawIcon(Codicon.DebugStackframe, centreX, centreY, size,
				ExecutionIsTopFrame ? Theme.DebugStackframeForeground : Theme.DebugStackframeFocusedForeground);
		}
	}

	private void DrawExecutionLine(IPainter painter, int line, float x, float lineTop, float width,
		float lineHeight) {
		if (line != ExecutionLine) return;

		painter.FillRectangle(x, lineTop, width, lineHeight,
			ExecutionIsTopFrame ? Theme.DebugExecutionBackground : Theme.DebugFocusedFrameBackground);
	}

	public readonly record struct Decoration(int Line, int StartColumn, int EndColumn, uint Color);

	public IReadOnlyList<SemanticSpan>? Semantics { get; set; }

	public readonly record struct SemanticSpan(int Line, int StartColumn, int Length, uint Color);

	private static int FirstSemantic(IReadOnlyList<SemanticSpan> spans, int line) {
		int low = 0;
		int high = spans.Count;

		while (low < high) {
			int middle = (low + high) / 2;

			if (spans[middle].Line < line) low = middle + 1;
			else high = middle;
		}

		return low;
	}

	private void DrawDecorations(IPainter painter, int line, float textLeft, float lineTop, float lineHeight) {
		if (Decorations is not { Count: > 0 } decorations) return;

		int index = FirstDecoration(decorations, line);

		TextSpan lineSpan = Snapshot.GetLineSpan(line);
		float cellWidth = painter.GetCode().GetCellWidth();

		for (int i = index; i < decorations.Count && decorations[i].Line == line; i++) {
			Decoration decoration = decorations[i];

			int start = Math.Min(lineSpan.Start + decoration.StartColumn, lineSpan.End);
			int end = Math.Min(lineSpan.Start + decoration.EndColumn, lineSpan.End);

			float left = textLeft + (VisualColumnAt(line, start) * cellWidth);
			float width = (VisualColumnAt(line, end) - VisualColumnAt(line, start)) * cellWidth;

			if (width > 0f) {
				painter.FillRectangle(left, lineTop, width, lineHeight,
					Color.FromBytes(
						(byte)(decoration.Color >> 16),
						(byte)(decoration.Color >> 8),
						(byte)decoration.Color,
						(byte)(decoration.Color >> 24)));
			}
		}
	}

	private static int FirstDecoration(IReadOnlyList<Decoration> decorations, int line) {
		int low = 0;
		int high = decorations.Count;

		while (low < high) {
			int middle = (low + high) / 2;

			if (decorations[middle].Line < line) low = middle + 1;
			else high = middle;
		}

		return low;
	}

	private void DrawIndentGuides(IPainter painter, int line, float textLeft, float lineTop, float lineHeight,
		int caretLine) {
		TextSpan span = Snapshot.GetLineSpan(line);
		if (span.Length == 0) return;

		EnsureCapacity(ref _raw, span.Length);
		Snapshot.CopyTo(span.Start, span.Length, _raw);

		int column = 0;
		int i = 0;
		while (i < span.Length && (_raw[i] == ' ' || _raw[i] == '\t')) {
			column = _raw[i] == '\t' ? ((column / TabWidth) + 1) * TabWidth : column + 1;
			i++;
		}

		if (i >= span.Length) return;

		float cellWidth = painter.GetCode().GetCellWidth();
		for (int guide = TabWidth; guide < column; guide += TabWidth) {
			Color color = line == caretLine ? Theme.IndentGuideActive : Theme.IndentGuide;
			painter.FillRectangle(MathF.Round(textLeft + (guide * cellWidth)), lineTop, 1f, lineHeight, color);
		}
	}

	private void DrawScrollbar(IPainter painter, float x, float y, float width, float height, float lineHeight) {
		float contentHeight = ContentHeight(lineHeight);
		if (contentHeight <= height) return;

		float trackWidth = ScrollbarWidth;
		float trackX = x + width - trackWidth;

		float visibleFraction = height / contentHeight;
		float thumbHeight = Math.Max(ScrollbarMinThumb, height * visibleFraction);

		float maxScroll = Math.Max(1f, contentHeight - height);
		float progress = Math.Clamp(ScrollY / maxScroll, 0f, 1f);
		float thumbY = y + (progress * (height - thumbHeight));

		painter.FillRoundedRectangle(
			trackX + 2f,
			thumbY + 2f,
			trackWidth - 4f,
			thumbHeight - 4f,
			(trackWidth - 4f) / 2f,
			Theme.ScrollbarSlider);
	}

	private void DrawHorizontalScrollbar(IPainter painter, float x, float y, float width, float height,
		float gutterWidth) {
		if (_contentWidth <= _textAreaWidth) return;

		float trackHeight = ScrollbarWidth;
		float trackLeft = x + gutterWidth;
		float trackWidth = width - gutterWidth;
		float trackY = y + height - trackHeight;

		float visibleFraction = _textAreaWidth / _contentWidth;
		float thumbWidth = Math.Max(ScrollbarMinThumb, trackWidth * visibleFraction);

		float maxScroll = Math.Max(1f, _contentWidth - _textAreaWidth);
		float progress = Math.Clamp(ScrollX / maxScroll, 0f, 1f);
		float thumbX = trackLeft + (progress * (trackWidth - thumbWidth));

		painter.FillRoundedRectangle(
			thumbX + 2f,
			trackY + 2f,
			thumbWidth - 4f,
			trackHeight - 4f,
			(trackHeight - 4f) / 2f,
			Theme.ScrollbarSlider);
	}

	private void DrawSelection(IPainter painter, int line, TextSpan selection, float textLeft, float lineTop,
		float lineHeight) {
		TextSpan lineSpan = Snapshot.GetLineSpanWithTerminator(line);
		if (selection.End <= lineSpan.Start || selection.Start >= lineSpan.End) return;

		TextSpan visible = Snapshot.GetLineSpan(line);
		int from = Math.Max(selection.Start, visible.Start);
		int to = Math.Min(selection.End, visible.End);

		float startColumn = VisualColumnAt(line, from);
		float endColumn = VisualColumnAt(line, to);

		float width = (endColumn - startColumn) * painter.GetCode().GetCellWidth();
		if (selection.End > visible.End) width += painter.GetCode().GetCellWidth();

		if (width <= 0f) return;

		painter.FillRectangle(textLeft + (startColumn * painter.GetCode().GetCellWidth()), lineTop, width, lineHeight,
			Theme.SelectionBackground);
	}

	private void DrawCaret(IPainter painter, int caretLine, int firstLine, float textLeft, float viewportTop, float lineHeight) {
		if (caretLine < firstLine) return;

		float caretTop = viewportTop + TopOf(caretLine, lineHeight) - ScrollY;
		float caretX = textLeft + (VisualColumnAt(caretLine, CaretOffset) * painter.GetCode().GetCellWidth());

		painter.FillRectangle(MathF.Round(caretX), caretTop, CaretWidth, lineHeight, Theme.CursorForeground);
	}

	private void DrawChangeMark(IPainter painter, int line, float x, float lineTop, float lineHeight) {
		if (Diff is not { } diff || diff.GetPath() != Path || diff.At(line) is not { } change) return;

		float width = ChangeMarkWidth;

		switch (change.Kind) {
			case GitChangeKind.Added:
				painter.FillRectangle(x, lineTop, width, lineHeight, Theme.GitAddedForeground);
				break;

			case GitChangeKind.Modified:
				painter.FillRectangle(x, lineTop, width, lineHeight, Theme.GitModifiedForeground);
				break;

			case GitChangeKind.Deleted:
				float size = lineHeight / 3f;
				painter.FillTriangle(x, lineTop - size, x, lineTop + size, x + size, lineTop,
					Theme.GitDeletedForeground);
				break;
		}
	}

	private void DrawLineNumber(IPainter painter, int line, int caretLine, float x, float lineTop, float gutterWidth) {
		ITextFont font = painter.GetCode();

		Span<char> digits = stackalloc char[12];
		if (!(line + 1).TryFormat(digits, out int written)) return;

		float textX = x + gutterWidth - GutterPaddingRight - (written * font.GetCellWidth());

		Color color = line == caretLine ? Theme.LineNumberActiveForeground : Theme.LineNumberForeground;
		painter.DrawText(painter.GetCode(), digits[..written], textX, lineTop + font.GetBaseline(), color);
	}

	private void DrawLineText(IPainter painter, int line, float x, float lineTop) {
		TextSpan span = Snapshot.GetLineSpan(line);
		if (span.Length == 0) return;

		EnsureCapacity(ref _raw, span.Length);
		Snapshot.CopyTo(span.Start, span.Length, _raw);

		int length = ExpandTabs(span.Length);
		float baseline = lineTop + painter.GetCode().GetBaseline();

		float lineWidth = length * painter.GetCode().GetCellWidth();
		if (lineWidth > _contentWidth) _contentWidth = lineWidth;

		if (!Highlighter.IsEnabled) {
			painter.DrawText(painter.GetCode(), _expanded.AsSpan(0, length), x, baseline, Theme.EditorForeground);
			return;
		}

		if (Highlighter.ColorLine(Snapshot, line) is { Count: > 0 } coloured) {
			float grammarCellWidth = painter.GetCode().GetCellWidth();

			foreach (Highlighter.ColoredToken token in coloured) {
				if (token.Length <= 0 || token.Start + token.Length > span.Length) continue;

				int from = _columns[token.Start];
				int to = _columns[token.Start + token.Length];

				if (to > from) {
					painter.DrawText(painter.GetCode(), _expanded.AsSpan(from, to - from),
						x + (from * grammarCellWidth), baseline, token.Color);
				}
			}

			return;
		}

		List<Token> tokens = Highlighter.TokenizeLine(Snapshot, line);

		if (tokens.Count == 0 && Semantics is not { Count: > 0 }) {
			painter.DrawText(painter.GetCode(), _expanded.AsSpan(0, length), x, baseline, Theme.EditorForeground);
			return;
		}

		float cellWidth = painter.GetCode().GetCellWidth();

		EnsureCapacity(ref _charColors, span.Length + 1);

		uint fallback = Pack(Theme.EditorForeground);

		for (int i = 0; i < span.Length; i++) _charColors[i] = fallback;

		for (int i = 0; i < tokens.Count; i++) {
			Token token = tokens[i];

			if (token.Length <= 0 || token.End > span.Length) continue;

			uint colour = Pack(SyntaxColors.For(token.Kind));

			for (int at = token.Start; at < token.End; at++) _charColors[at] = colour;
		}

		if (Semantics is { Count: > 0 } semantics) {
			for (int i = FirstSemantic(semantics, line);
				i < semantics.Count && semantics[i].Line == line; i++) {
				SemanticSpan semantic = semantics[i];
				int end = Math.Min(semantic.StartColumn + semantic.Length, span.Length);

				for (int at = Math.Max(0, semantic.StartColumn); at < end; at++) _charColors[at] = semantic.Color;
			}
		}

		int runStart = 0;

		for (int i = 1; i <= span.Length; i++) {
			if (i < span.Length && _charColors[i] == _charColors[runStart]) continue;

			int from = _columns[runStart];
			int to = _columns[i];

			if (to > from) {
				painter.DrawText(painter.GetCode(),
					_expanded.AsSpan(from, to - from),
					x + (from * cellWidth),
					baseline,
					Unpack(_charColors[runStart]));
			}

			runStart = i;
		}
	}

	private static uint Pack(Color colour) =>
		((uint)(colour.A * 255f) << 24) | ((uint)(colour.R * 255f) << 16) |
		((uint)(colour.G * 255f) << 8) | (uint)(colour.B * 255f);

	private static Color Unpack(uint packed) =>
		Color.FromBytes((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed, (byte)(packed >> 24));

	private uint[] _charColors = new uint[256];

	private int ExpandTabs(int length) {
		EnsureCapacity(ref _expanded, length + (TabWidth * 8));
		EnsureCapacity(ref _columns, length + 1);

		int column = 0;
		for (int i = 0; i < length; i++) {
			_columns[i] = column;

			char character = _raw[i];
			if (character == '\t') {
				int stop = ((column / TabWidth) + 1) * TabWidth;
				EnsureCapacity(ref _expanded, stop + (length - i) + TabWidth);
				while (column < stop) _expanded[column++] = ' ';
			}
			else {
				EnsureCapacity(ref _expanded, column + 1 + (length - i));
				_expanded[column++] = character;
			}
		}

		_columns[length] = column;
		return column;
	}

	private static void EnsureCapacity(ref uint[] buffer, int required) {
		if (required <= buffer.Length) return;

		int capacity = buffer.Length;

		while (capacity < required) capacity *= 2;

		buffer = new uint[capacity];
	}

	private static void EnsureCapacity(ref int[] buffer, int required) {
		if (required <= buffer.Length) return;

		int capacity = buffer.Length;
		while (capacity < required) capacity *= 2;

		Array.Resize(ref buffer, capacity);
	}

	#endregion

	private static void EnsureCapacity(ref char[] buffer, int required) {
		if (required <= buffer.Length) return;

		int capacity = buffer.Length;
		while (capacity < required) capacity *= 2;

		Array.Resize(ref buffer, capacity);
	}

	private static int DigitCount(int value) {
		int digits = 1;
		while (value >= 10) {
			value /= 10;
			digits++;
		}

		return digits;
	}
}
