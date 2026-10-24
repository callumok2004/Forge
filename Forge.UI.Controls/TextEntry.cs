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

	public string Placeholder { get; set; } = string.Empty;

	public Action? TextChanged { get; set; }

	public ReadOnlySpan<char> Text => _buffer.AsSpan(0, _length);

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
				MoveCaret(0);
				return true;

			case VirtualKey.End:
				MoveCaret(_length);
				return true;

			case VirtualKey.A when control:
				_allSelected = _length > 0;
				Repaint();
				return true;

			case VirtualKey.V when control:
				if (_allSelected) DeleteAll();

				Insert(Clipboard.GetText().ReplaceLineEndings(" "));
				return true;

			case VirtualKey.Back or VirtualKey.Delete:
				return true;
		}

		return false;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		RequestFocus();
		_allSelected = false;

		ITextFont font = Ui!.GetFonts().GetUi();
		float textLeft = Metrics.Space6 * Scale;
		int caret = 0;
		float advance = textLeft;

		while (caret < _length && advance + (font.GetAdvance(_buffer[caret]) / 2f) < x) advance += font.GetAdvance(_buffer[caret++]);

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
		ITextFont font = painter.GetUi();
		float radius = Metrics.RadiusSmall * scale;
		bool focused = HasFocus;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.InputBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, focused ? Theme.FocusBorder : Theme.InputBorder);

		float textLeft = ScreenX + (Metrics.Space6 * scale);
		float baseline = ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.PushClip(textLeft, ScreenY, MathF.Max(0f, Wide - (Metrics.Space12 * scale)), Tall);

		if (_allSelected) painter.FillRectangle(textLeft, baseline - font.GetBaseline(), font.Measure(Text), font.GetLineHeight(), Theme.SelectionBackground);

		if (_length == 0) painter.DrawText(font, Placeholder, textLeft, baseline, Theme.InputPlaceholderForeground);
		else painter.DrawText(font, Text, textLeft, baseline, Theme.InputForeground);

		if (focused && _caretVisible) painter.FillRectangle(textLeft + font.Measure(Text[.._caret]), baseline - font.GetBaseline() + (2f * scale), Metrics.StrokeThickness * scale, font.GetLineHeight() - (4f * scale), Theme.CursorForeground);

		painter.PopClip();
	}
}
