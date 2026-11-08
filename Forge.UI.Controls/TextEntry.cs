using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class TextEntry : Panel
{
	private const int BlinkMilliseconds = 530;

	private char[] _buffer = new char[64];
	private int _length;
	private int _caret;
	private bool _caretVisible = true;
	private bool _allSelected;

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

	public void SelectAll() {
		_allSelected = _length > 0;
		_caret = _length;
		Repaint();
	}

	public void SetText(ReadOnlySpan<char> text) {
		if (text.SequenceEqual(Text)) return;

		if (text.Length > _buffer.Length) Array.Resize(ref _buffer, text.Length);

		text.CopyTo(_buffer);
		_length = text.Length;
		_caret = _length;
		_allSelected = false;
		Changed();
	}

	public Action? TextChanged { get; set; }

	public Action? Submitted { get; set; }

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

	private void MoveVertical(int direction) {
		int start = LineStart(_caret);
		int column = _caret - start;
		int target;

		if (direction < 0) {
			if (start == 0) {
				MoveCaret(0);
				return;
			}

			target = LineStart(start - 1);
		}
		else {
			int end = LineEnd(_caret);

			if (end == _length) {
				MoveCaret(_length);
				return;
			}

			target = end + 1;
		}

		MoveCaret(Math.Min(target + column, LineEnd(target)));
	}

	private ITextFont FontOf(IFontSet fonts) => FontSize > 0f ? fonts.GetUi(FontSize) : fonts.GetUi();

	public ReadOnlySpan<char> Text => _buffer.AsSpan(0, _length);

	public string GetString() => new(Text);

	public void Append(char character) => Insert([character]);

	public void Clear() {
		if (_length == 0) return;

		_length = 0;
		_caret = 0;
		_allSelected = false;
		Changed();
	}

	public override void OnSetFocus() {
		_caretVisible = true;
		AddTickSignal(BlinkMilliseconds);
		Repaint();
	}

	public override void OnKillFocus(Panel? newFocus) {
		RemoveTickSignal();
		Submitted?.Invoke();
		Repaint();
	}

	public override void OnTick() {
		_caretVisible = !_caretVisible;
		Repaint();
	}

	public override bool OnKeyTyped(char character) {
		if (_allSelected) DeleteAll();

		Insert([character]);
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		bool control = (modifiers & KeyModifiers.Control) != 0;

		switch (key) {
			case VirtualKey.Return when Multiline && !control:
				if (_allSelected) DeleteAll();

				Insert(['\n']);
				return true;

			case VirtualKey.Up when Multiline:
				MoveVertical(-1);
				return true;

			case VirtualKey.Down when Multiline:
				MoveVertical(1);
				return true;

			case VirtualKey.Return when Submitted is not null:
				Submitted();
				return true;

			case VirtualKey.Back when _allSelected:
			case VirtualKey.Delete when _allSelected:
				DeleteAll();
				return true;

			case VirtualKey.Back when _caret > 0:
				Remove(control ? WordStart() : _caret - 1, _caret);
				return true;

			case VirtualKey.Delete when _caret < _length:
				Remove(_caret, _caret + 1);
				return true;

			case VirtualKey.Left:
				MoveCaret(control ? WordStart() : _caret - 1);
				return true;

			case VirtualKey.Right:
				MoveCaret(_caret + 1);
				return true;

			case VirtualKey.Home:
				MoveCaret(Multiline && !control ? LineStart(_caret) : 0);
				return true;

			case VirtualKey.End:
				MoveCaret(Multiline && !control ? LineEnd(_caret) : _length);
				return true;

			case VirtualKey.A when control:
				_allSelected = _length > 0;
				Repaint();
				return true;

			case VirtualKey.V when control:
				if (_allSelected) DeleteAll();

				Insert(Clipboard.GetText().ReplaceLineEndings(Multiline ? "\n" : " "));
				return true;

			case VirtualKey.Back or VirtualKey.Delete:
				return true;
		}

		return false;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		RequestFocus();
		_allSelected = false;

		ITextFont font = FontOf(Ui!.GetFonts());
		float textLeft = Metrics.Space6 * Scale;
		int caret = 0;
		int limit = _length;

		if (Multiline) {
			float lineHeight = font.GetLineHeight();
			int line = _firstLine + Math.Max(0, (int)((y - ((Tall - (VisibleLines * lineHeight)) / 2f)) / lineHeight));

			for (int i = 0; i < line && caret < _length; i++) caret = LineEnd(caret) + (LineEnd(caret) < _length ? 1 : 0);

			limit = LineEnd(caret);
		}

		float advance = textLeft;

		while (caret < limit && advance + (font.GetAdvance(_buffer[caret]) / 2f) < x) advance += font.GetAdvance(_buffer[caret++]);

		MoveCaret(caret);
		return true;
	}

	private void Insert(ReadOnlySpan<char> text) {
		if (_length + text.Length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + text.Length));

		_buffer.AsSpan(_caret, _length - _caret).CopyTo(_buffer.AsSpan(_caret + text.Length));
		text.CopyTo(_buffer.AsSpan(_caret));
		_length += text.Length;
		_caret += text.Length;
		Changed();
	}

	private void Remove(int start, int end) {
		_buffer.AsSpan(end, _length - end).CopyTo(_buffer.AsSpan(start));
		_length -= end - start;
		_caret = start;
		Changed();
	}

	private void DeleteAll() {
		_allSelected = false;
		_length = 0;
		_caret = 0;
		Changed();
	}

	private int WordStart() {
		int index = _caret;

		while (index > 0 && char.IsWhiteSpace(_buffer[index - 1])) index--;

		while (index > 0 && !char.IsWhiteSpace(_buffer[index - 1])) index--;

		return index;
	}

	private void MoveCaret(int caret) {
		_allSelected = false;
		_caret = Math.Clamp(caret, 0, _length);
		_caretVisible = true;
		Repaint();
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

		float baseline = ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.PushClip(textLeft, ScreenY, MathF.Max(0f, Wide - (Metrics.Space12 * scale) - (TextInset.Right * scale)), Tall);

		if (_allSelected) painter.FillRectangle(textLeft, baseline - font.GetBaseline(), font.Measure(Text), font.GetLineHeight(), Theme.SelectionBackground);

		if (_length == 0) painter.DrawText(font, Placeholder, textLeft, baseline, Theme.InputPlaceholderForeground);
		else painter.DrawText(font, Text, textLeft, baseline, Theme.InputForeground);

		if (focused && _caretVisible) painter.FillRectangle(textLeft + font.Measure(Text[.._caret]), baseline - font.GetBaseline() + (2f * scale), Metrics.StrokeThickness * scale, font.GetLineHeight() - (4f * scale), Theme.CursorForeground);

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

		painter.PushClip(textLeft, ScreenY, MathF.Max(0f, Wide - (Metrics.Space12 * scale) - (TextInset.Right * scale)), Tall);

		if (_length == 0) painter.DrawText(font, Placeholder, textLeft, top + font.GetBaseline(), Theme.InputPlaceholderForeground);

		int start = 0;
		int line = 0;

		while (true) {
			int end = LineEnd(start);

			if (line >= _firstLine && line < _firstLine + visible) {
				float y = top + ((line - _firstLine) * lineHeight);
				ReadOnlySpan<char> text = Text[start..end];

				if (_allSelected) painter.FillRectangle(textLeft, y, MathF.Max(font.Measure(text), font.GetAdvance(' ')), lineHeight, Theme.SelectionBackground);

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
