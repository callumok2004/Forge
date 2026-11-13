using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class TextEntry : Panel
{
	private const int BlinkMilliseconds = 530;
	private const int UndoLimit = 200;

	private enum EditKind { None, Typing, Deleting, Other }

	private readonly record struct Snapshot(string Text, int Anchor, int Caret);

	private char[] _buffer = new char[64];
	private int _length;
	private int _caret;
	private int _anchor;
	private bool _caretVisible = true;
	private float _scrollX;
	private int _dragMode;
	private int _dragStart;
	private int _dragEnd;
	private EditKind _lastEdit;
	private readonly List<Snapshot> _undo = [];
	private readonly List<Snapshot> _redo = [];

	public TextEntry() {
		KeyboardInputEnabled = true;
		Cursor = CursorCode.IBeam;
	}

	private static readonly Color InvalidBorder = Color.Parse("#BE1100");

	public string Placeholder { get; set; } = string.Empty;

	public bool Invalid {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public Thickness TextInset { get; set; } = Thickness.Zero;

	private int SelectionStart => Math.Min(_anchor, _caret);

	private int SelectionEnd => Math.Max(_anchor, _caret);

	private bool HasSelection => _anchor != _caret;

	public void SelectAll() => Select(0, _length);

	public void SetText(ReadOnlySpan<char> text) {
		if (text.SequenceEqual(Text)) return;

		if (text.Length > _buffer.Length) Array.Resize(ref _buffer, text.Length);

		text.CopyTo(_buffer);
		_length = text.Length;
		_caret = _length;
		_anchor = _length;
		_undo.Clear();
		_redo.Clear();
		_lastEdit = EditKind.None;
		Changed();
	}

	public Action? TextChanged { get; set; }

	public Action? Submitted { get; set; }

	public Action? FocusLost { get; set; }

	public float FontSize { get; init; }

	public bool Multiline { get; init; }

	public int MaxVisibleLines { get; init; } = 10;

	private int _firstLine;

	public int LineCount => Text.Count('\n') + 1;

	public int VisibleLines => Math.Clamp(LineCount, 1, MaxVisibleLines);

	public float GetLineHeight() => FontOf(Ui!.GetFonts()).GetLineHeight();

	private int LineStart(int offset) => Text[..offset].LastIndexOf('\n') + 1;

	private int LineEnd(int offset) {
		int index = Text[offset..].IndexOf('\n');
		return index < 0 ? _length : offset + index;
	}

	private int LineOf(int offset) => Text[..offset].Count('\n');

	private int Vertical(int direction) {
		int start = LineStart(_caret);
		int column = _caret - start;
		int target;

		if (direction < 0) {
			if (start == 0) return 0;

			target = LineStart(start - 1);
		}
		else {
			int end = LineEnd(_caret);

			if (end == _length) return _length;

			target = end + 1;
		}

		return Math.Min(target + column, LineEnd(target));
	}

	private ITextFont FontOf(IFontSet fonts) => FontSize > 0f ? fonts.GetUi(FontSize) : fonts.GetUi();

	public ReadOnlySpan<char> Text => _buffer.AsSpan(0, _length);

	public string GetString() => new(Text);

	public void Append(char character) => Replace([character], EditKind.Typing);

	public void Clear() {
		if (_length == 0) return;

		Record(EditKind.Other);
		_length = 0;
		_caret = 0;
		_anchor = 0;
		Changed();
	}

	public override void OnSetFocus() {
		_caretVisible = true;
		AddTickSignal(BlinkMilliseconds);
		Repaint();
	}

	public override void OnKillFocus(Panel? newFocus) {
		RemoveTickSignal();
		_dragMode = 0;
		(FocusLost ?? Submitted)?.Invoke();
		Repaint();
	}

	public override void OnTick() {
		_caretVisible = !_caretVisible;
		Repaint();
	}

	public override bool OnKeyTyped(char character) {
		Replace([character], EditKind.Typing);
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		bool control = (modifiers & KeyModifiers.Control) != 0;
		bool shift = (modifiers & KeyModifiers.Shift) != 0;
		bool alt = (modifiers & KeyModifiers.Alt) != 0;

		if (alt) return false;

		switch (key) {
			case VirtualKey.Return when Multiline && !control:
				Replace(['\n'], EditKind.Typing);
				return true;

			case VirtualKey.Up when Multiline:
				MoveTo(Vertical(-1), shift);
				return true;

			case VirtualKey.Down when Multiline:
				MoveTo(Vertical(1), shift);
				return true;

			case VirtualKey.Return when Submitted is not null:
				Submitted();
				return true;

			case VirtualKey.Back:
				if (HasSelection) Replace([], EditKind.Deleting);
				else if (_caret > 0) Delete(control ? WordLeft(_caret) : _caret - 1, _caret);

				return true;

			case VirtualKey.Delete when shift && !control:
				Cut();
				return true;

			case VirtualKey.Delete:
				if (HasSelection) Replace([], EditKind.Deleting);
				else if (_caret < _length) Delete(_caret, control ? WordRight(_caret) : _caret + 1);

				return true;

			case VirtualKey.Left:
				if (!shift && HasSelection && !control) MoveTo(SelectionStart, false);
				else MoveTo(control ? WordLeft(_caret) : _caret - 1, shift);

				return true;

			case VirtualKey.Right:
				if (!shift && HasSelection && !control) MoveTo(SelectionEnd, false);
				else MoveTo(control ? WordRight(_caret) : _caret + 1, shift);

				return true;

			case VirtualKey.Home:
				MoveTo(Multiline && !control ? LineStart(_caret) : 0, shift);
				return true;

			case VirtualKey.End:
				MoveTo(Multiline && !control ? LineEnd(_caret) : _length, shift);
				return true;

			case VirtualKey.A when control:
				SelectAll();
				return true;

			case VirtualKey.C when control:
			case VirtualKey.Insert when control && !shift:
				Copy();
				return true;

			case VirtualKey.X when control:
				Cut();
				return true;

			case VirtualKey.V when control:
			case VirtualKey.Insert when shift && !control:
				Paste();
				return true;

			case VirtualKey.Z when control && shift:
			case VirtualKey.Y when control:
				Redo();
				return true;

			case VirtualKey.Z when control:
				Undo();
				return true;
		}

		return false;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		RequestFocus();

		if (button != MouseButton.Left) return true;

		int offset = OffsetAt(x, y);

		if (Win32.IsKeyDown(Win32.VkShift)) MoveTo(offset, true);
		else MoveTo(offset, false);

		BeginDrag(1, _anchor, _anchor);
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return OnMousePressed(button, x, y);

		RequestFocus();
		(int start, int end) = WordAt(OffsetAt(x, y));
		Select(start, end);
		BeginDrag(2, start, end);
		return true;
	}

	public override bool OnMouseTriplePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return OnMousePressed(button, x, y);

		RequestFocus();

		if (Multiline) {
			int offset = OffsetAt(x, y);
			int start = LineStart(offset);
			int end = Math.Min(_length, LineEnd(offset) + 1);
			Select(start, end);
			BeginDrag(3, start, end);
		}
		else {
			SelectAll();
		}

		return true;
	}

	public override void OnCursorMoved(float x, float y) {
		if (_dragMode == 0) return;

		int offset = OffsetAt(x, y);

		switch (_dragMode) {
			case 1:
				SetSelection(_dragStart, offset);
				break;

			case 2: {
				(int start, int end) = WordAt(offset);

				if (offset < _dragStart) SetSelection(_dragEnd, start);
				else SetSelection(_dragStart, Math.Max(end, _dragEnd));

				break;
			}

			case 3: {
				int start = LineStart(offset);
				int end = Math.Min(_length, LineEnd(offset) + 1);

				if (start < _dragStart) SetSelection(_dragEnd, start);
				else SetSelection(_dragStart, Math.Max(end, _dragEnd));

				break;
			}
		}
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (_dragMode == 0) return false;

		_dragMode = 0;

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		return true;
	}

	public override void OnMouseCaptureLost() => _dragMode = 0;

	private void BeginDrag(int mode, int start, int end) {
		_dragMode = mode;
		_dragStart = start;
		_dragEnd = end;
		Ui?.SetMouseCapture(this);
	}

	private int OffsetAt(float x, float y) {
		ITextFont font = FontOf(Ui!.GetFonts());
		int caret = 0;
		int limit = _length;
		float advance = (Metrics.Space6 * Scale) - _scrollX;

		if (Multiline) {
			float lineHeight = font.GetLineHeight();
			int line = _firstLine + Math.Max(0, (int)MathF.Floor((y - ((Tall - (VisibleLines * lineHeight)) / 2f)) / lineHeight));

			for (int i = 0; i < line && caret < _length; i++) {
				int end = LineEnd(caret);

				if (end >= _length) break;

				caret = end + 1;
			}

			limit = LineEnd(caret);
			advance = Metrics.Space6 * Scale;
		}

		while (caret < limit && advance + (font.GetAdvance(_buffer[caret]) / 2f) < x) advance += font.GetAdvance(_buffer[caret++]);

		return caret;
	}

	private static int Class(char character) => char.IsWhiteSpace(character) ? 0 : char.IsLetterOrDigit(character) || character == '_' ? 1 : 2;

	private (int Start, int End) WordAt(int offset) {
		if (_length == 0) return (0, 0);

		int probe = offset < _length && (offset == 0 || _buffer[offset] != '\n') ? offset : offset - 1;

		if (probe < 0 || _buffer[probe] == '\n') return (offset, offset);

		int kind = Class(_buffer[probe]);
		int start = probe;
		int end = probe + 1;

		while (start > 0 && _buffer[start - 1] != '\n' && Class(_buffer[start - 1]) == kind) start--;

		while (end < _length && _buffer[end] != '\n' && Class(_buffer[end]) == kind) end++;

		return (start, end);
	}

	private int WordLeft(int index) {
		while (index > 0 && char.IsWhiteSpace(_buffer[index - 1]) && _buffer[index - 1] != '\n') index--;

		if (index > 0 && _buffer[index - 1] == '\n') return index - 1;

		if (index == 0) return 0;

		int kind = Class(_buffer[index - 1]);

		while (index > 0 && _buffer[index - 1] != '\n' && Class(_buffer[index - 1]) == kind) index--;

		return index;
	}

	private int WordRight(int index) {
		if (index < _length && _buffer[index] == '\n') return index + 1;

		while (index < _length && char.IsWhiteSpace(_buffer[index]) && _buffer[index] != '\n') index++;

		if (index >= _length || _buffer[index] == '\n') return index;

		int kind = Class(_buffer[index]);

		while (index < _length && _buffer[index] != '\n' && Class(_buffer[index]) == kind) index++;

		return index;
	}

	private void Copy() {
		if (HasSelection) Clipboard.SetText(Text[SelectionStart..SelectionEnd]);
	}

	private void Cut() {
		if (!HasSelection) return;

		Copy();
		Replace([], EditKind.Other);
	}

	private void Paste() {
		string text = Clipboard.GetText();

		if (text.Length == 0) return;

		Replace(text.ReplaceLineEndings(Multiline ? "\n" : " "), EditKind.Other);
	}

	private void Record(EditKind kind) {
		if (kind == EditKind.Other || kind != _lastEdit) {
			_undo.Add(new Snapshot(GetString(), _anchor, _caret));

			if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
		}

		_redo.Clear();
		_lastEdit = kind;
	}

	private void Undo() {
		if (_undo.Count == 0) return;

		_redo.Add(new Snapshot(GetString(), _anchor, _caret));
		Restore(_undo[^1]);
		_undo.RemoveAt(_undo.Count - 1);
	}

	private void Redo() {
		if (_redo.Count == 0) return;

		_undo.Add(new Snapshot(GetString(), _anchor, _caret));
		Restore(_redo[^1]);
		_redo.RemoveAt(_redo.Count - 1);
	}

	private void Restore(Snapshot snapshot) {
		if (snapshot.Text.Length > _buffer.Length) Array.Resize(ref _buffer, snapshot.Text.Length);

		snapshot.Text.CopyTo(_buffer);
		_length = snapshot.Text.Length;
		_anchor = Math.Min(snapshot.Anchor, _length);
		_caret = Math.Min(snapshot.Caret, _length);
		_lastEdit = EditKind.None;
		Changed();
	}

	private void Replace(ReadOnlySpan<char> text, EditKind kind) {
		if (text.IsEmpty && !HasSelection) return;

		Record(kind);

		int start = SelectionStart;
		int end = SelectionEnd;
		int length = _length - (end - start) + text.Length;

		if (length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, length));

		_buffer.AsSpan(end, _length - end).CopyTo(_buffer.AsSpan(start + text.Length));
		text.CopyTo(_buffer.AsSpan(start));
		_length = length;
		_caret = start + text.Length;
		_anchor = _caret;
		Changed();
	}

	private void Delete(int start, int end) {
		_anchor = Math.Clamp(start, 0, _length);
		_caret = Math.Clamp(end, 0, _length);
		Replace([], EditKind.Deleting);
	}

	private void Select(int anchor, int caret) {
		_lastEdit = EditKind.None;
		_anchor = Math.Clamp(anchor, 0, _length);
		_caret = Math.Clamp(caret, 0, _length);
		_caretVisible = true;
		Repaint();
	}

	private void SetSelection(int anchor, int caret) {
		if (anchor == _anchor && caret == _caret) return;

		Select(anchor, caret);
	}

	private void MoveTo(int caret, bool extend) {
		caret = Math.Clamp(caret, 0, _length);
		Select(extend ? _anchor : caret, caret);
	}

	private void Changed() {
		_caretVisible = true;
		Repaint();
		TextChanged?.Invoke();
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = FontSize > 0f ? painter.GetUi(FontSize) : painter.GetUi();
		float radius = Metrics.RadiusSmall * scale;
		bool focused = HasFocus;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.InputBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Invalid ? InvalidBorder : focused ? Theme.FocusBorder : Theme.InputBorder);

		float textLeft = ScreenX + (Metrics.Space6 * scale);

		if (Multiline) {
			PaintLines(painter, font, textLeft, focused);
			return;
		}

		float clipWidth = MathF.Max(0f, Wide - (Metrics.Space12 * scale) - (TextInset.Right * scale));
		float caretX = font.Measure(Text[.._caret]);
		float cursorWidth = Metrics.StrokeThickness * scale;

		if (caretX - _scrollX > clipWidth - cursorWidth) _scrollX = caretX - clipWidth + cursorWidth;
		else if (caretX < _scrollX) _scrollX = caretX;

		_scrollX = MathF.Max(0f, MathF.Min(_scrollX, MathF.Max(0f, font.Measure(Text) - clipWidth + cursorWidth)));

		float baseline = ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float origin = textLeft - _scrollX;

		painter.PushClip(textLeft, ScreenY, clipWidth, Tall);

		if (HasSelection) {
			float from = font.Measure(Text[..SelectionStart]);
			float to = font.Measure(Text[..SelectionEnd]);
			painter.FillRectangle(origin + from, baseline - font.GetBaseline(), to - from, font.GetLineHeight(), Theme.SelectionBackground);
		}

		if (_length == 0) painter.DrawText(font, Placeholder, textLeft, baseline, Theme.InputPlaceholderForeground);
		else painter.DrawText(font, Text, origin, baseline, Theme.InputForeground);

		if (focused && _caretVisible) painter.FillRectangle(origin + caretX, baseline - font.GetBaseline() + (2f * scale), cursorWidth, font.GetLineHeight() - (4f * scale), Theme.CursorForeground);

		painter.PopClip();
	}

	private void PaintLines(IPainter painter, ITextFont font, float textLeft, bool focused) {
		float scale = Scale;
		float lineHeight = font.GetLineHeight();
		int visible = VisibleLines;
		int caretLine = LineOf(_caret);

		if (caretLine < _firstLine) _firstLine = caretLine;
		else if (caretLine >= _firstLine + visible) _firstLine = caretLine - visible + 1;

		_firstLine = Math.Clamp(_firstLine, 0, Math.Max(0, LineCount - visible));

		float top = ScreenY + ((Tall - (visible * lineHeight)) / 2f);
		int selectionStart = SelectionStart;
		int selectionEnd = SelectionEnd;
		Color selection = Theme.SelectionBackground;

		painter.PushClip(textLeft, ScreenY, MathF.Max(0f, Wide - (Metrics.Space12 * scale) - (TextInset.Right * scale)), Tall);

		if (_length == 0) painter.DrawText(font, Placeholder, textLeft, top + font.GetBaseline(), Theme.InputPlaceholderForeground);

		int start = 0;
		int line = 0;

		while (true) {
			int end = LineEnd(start);

			if (line >= _firstLine && line < _firstLine + visible) {
				float y = top + ((line - _firstLine) * lineHeight);
				ReadOnlySpan<char> text = Text[start..end];

				if (selectionStart != selectionEnd && selectionStart <= end && selectionEnd >= start) {
					int from = Math.Max(selectionStart, start);
					int to = Math.Min(selectionEnd, end);
					float left = font.Measure(Text[start..from]);
					float width = font.Measure(Text[from..to]) + (selectionEnd > end ? font.GetAdvance(' ') : 0f);

					if (width > 0f) painter.FillRectangle(textLeft + left, y, width, lineHeight, selection);
				}

				if (text.Length > 0) painter.DrawText(font, text, textLeft, y + font.GetBaseline(), Theme.InputForeground);

				if (focused && _caretVisible && line == caretLine) painter.FillRectangle(textLeft + font.Measure(Text[start.._caret]), y + (2f * scale), Metrics.StrokeThickness * scale, lineHeight - (4f * scale), Theme.CursorForeground);
			}

			if (end >= _length) break;

			start = end + 1;
			line++;
		}

		painter.PopClip();
	}
}
