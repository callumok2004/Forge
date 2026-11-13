using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.Workbench;

public sealed class OutputView : Panel
{
	private const float PaddingX = 12f;

	private readonly PanelPart _part;
	private readonly string[] _lines = new string[256];

	private float _scrollY;
	private (int Line, int Column) _anchor = (-1, 0);
	private (int Line, int Column) _focus = (-1, 0);
	private bool _selecting;
	private int _firstLine;
	private int _count;
	private float _lineHeight = 1f;
	private int _visible = 1;
	private ITextFont? _font;

	public OutputView(PanelPart part) {
		_part = part;
		Name = "Output";
		Dock = DockType.Fill;
		Cursor = CursorCode.IBeam;
		KeyboardInputEnabled = true;
	}

	private IOutputChannel Channel => _part.ActiveChannel;

	private bool HasSelection => _anchor.Line >= 0 && _anchor != _focus;

	public void ResetScroll() {
		_scrollY = 0f;
		ClearSelection();
	}

	public void ClearSelection() {
		_anchor = _focus = (-1, 0);
		Repaint();
	}

	public void SelectAll() {
		int total = Channel.GetCount();

		if (total == 0) return;

		_anchor = (0, 0);
		_focus = (total - 1, int.MaxValue);
		Repaint();
	}

	private ((int Line, int Column) Start, (int Line, int Column) End) Ordered() =>
		_anchor.Line < _focus.Line || (_anchor.Line == _focus.Line && _anchor.Column <= _focus.Column) ? (_anchor, _focus) : (_focus, _anchor);

	public string Copy() {
		if (!HasSelection) return string.Empty;

		((int startLine, int startColumn), (int endLine, int endColumn)) = Ordered();
		return Channel.ReadRange(startLine, startColumn, endLine, endColumn);
	}

	private ReadOnlySpan<char> VisibleLine(int line) {
		int index = line - _firstLine;
		return (uint)index < (uint)_count ? _lines[index] : default;
	}

	private (int Line, int Column) PositionAt(float x, float y) {
		int total = Channel.GetCount();

		if (total == 0 || _font is null) return (-1, 0);

		int line = Math.Clamp(_firstLine + (int)MathF.Floor(y / _lineHeight), 0, total - 1);
		ReadOnlySpan<char> text = VisibleLine(line);
		float cursor = PaddingX * Scale;

		for (int i = 0; i < text.Length; i++) {
			float advance = _font.GetAdvance(text[i]);

			if (x < cursor + (advance / 2f)) return (line, i);

			cursor += advance;
		}

		return (line, text.Length);
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		RequestFocus();

		if (button != MouseButton.Left) return true;

		(int Line, int Column) position = PositionAt(x, y);

		if ((Ui?.GetModifiers() & KeyModifiers.Shift) != 0 && _anchor.Line >= 0) _focus = position;
		else _anchor = _focus = position;

		_selecting = position.Line >= 0;

		if (_selecting) Ui?.SetMouseCapture(this);

		Repaint();
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		(int line, int column) = PositionAt(x, y);
		ReadOnlySpan<char> text = VisibleLine(line);

		if (line < 0 || text.IsEmpty) return true;

		(int start, int end) = WordRange.At(text, column, WordRange.EditorSeparators);
		_anchor = (line, start);
		_focus = (line, end);
		Repaint();
		return true;
	}

	public override bool OnMouseTriplePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		(int line, _) = PositionAt(x, y);

		if (line < 0) return true;

		_anchor = (line, 0);
		_focus = line + 1 < Channel.GetCount() ? (line + 1, 0) : (line, VisibleLine(line).Length);
		Repaint();
		return true;
	}

	public override void OnCursorMoved(float x, float y) {
		if (!_selecting) return;

		if (y < 0f) Scroll(_lineHeight);
		else if (y > Tall) Scroll(-_lineHeight);

		(int Line, int Column) position = PositionAt(x, Math.Clamp(y, 0f, Tall - 1f));

		if (position.Line < 0 || position == _focus) return;

		_focus = position;
		Repaint();
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		_selecting = false;

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		return true;
	}

	public override void OnMouseCaptureLost() => _selecting = false;

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if ((modifiers & KeyModifiers.Control) == 0) return false;

		if (key == VirtualKey.C) {
			if (HasSelection) Clipboard.SetText(Copy());

			return true;
		}

		if (key != VirtualKey.A) return false;

		SelectAll();
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		Scroll(delta / 120f * 3f * _lineHeight);
		return true;
	}

	private void Scroll(float amount) {
		float maximum = Math.Max(0f, (Channel.GetCount() - _visible) * _lineHeight);
		float next = Math.Clamp(_scrollY + amount, 0f, maximum);

		if (next == _scrollY) return;

		_scrollY = next;
		Repaint();
	}

	public override void Paint(IPainter painter) {
		ITextFont font = painter.GetCode();
		float scale = Scale;
		float rowHeight = font.GetLineHeight();
		IOutputChannel channel = Channel;
		int total = channel.GetCount();

		_font = font;
		_lineHeight = rowHeight;
		_visible = Math.Max(1, (int)(Tall / rowHeight));

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		if (total == 0) {
			_firstLine = 0;
			_count = 0;
			ITextFont ui = painter.GetUi();
			painter.DrawText(ui, $"No output yet from {channel.GetName()}.", ScreenX + (PaddingX * scale), ScreenY + (Metrics.Space12 * scale) + ui.GetBaseline(), Theme.SectionHeaderForeground);
			painter.PopClip();
			return;
		}

		int maxSkip = Math.Max(0, total - _visible);
		_scrollY = Math.Min(_scrollY, maxSkip * rowHeight);
		int skip = Math.Clamp(maxSkip - (int)Math.Round(_scrollY / rowHeight), 0, maxSkip);
		int count = channel.CopyTo(_lines, skip);
		bool selection = HasSelection;
		((int Line, int Column) start, (int Line, int Column) end) = selection ? Ordered() : ((-1, 0), (-1, 0));
		Color highlight = HasFocus ? Theme.SelectionBackground : Theme.SelectionBackground.WithAlpha(0.6f);
		float left = ScreenX + (PaddingX * scale);

		_firstLine = skip;
		_count = count;

		for (int i = 0; i < count && i <= _visible; i++) {
			float top = ScreenY + (i * rowHeight);
			int line = skip + i;
			ReadOnlySpan<char> text = _lines[i];

			if (selection && line >= start.Line && line <= end.Line) {
				int from = line == start.Line ? Math.Min(start.Column, text.Length) : 0;
				int to = line == end.Line ? Math.Min(end.Column, text.Length) : text.Length;
				float x = left + font.Measure(text[..from]);
				float width = font.Measure(text[from..to]);

				if (line != end.Line) width += font.GetAdvance(' ');

				if (width > 0f) painter.FillRectangle(x, top, width, rowHeight, highlight);
			}

			painter.DrawText(font, text, left, top + font.GetBaseline(), Theme.EditorForeground);
		}

		painter.PopClip();
	}
}
