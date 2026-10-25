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
	private int _anchor = -1;
	private int _focus = -1;
	private int _firstLine;
	private float _lineHeight = 1f;
	private int _visible = 1;

	public OutputView(PanelPart part) {
		_part = part;
		Name = "Output";
		Dock = DockType.Fill;
		Cursor = CursorCode.IBeam;
		KeyboardInputEnabled = true;
	}

	private IOutputChannel Channel => _part.ActiveChannel;

	public void ResetScroll() {
		_scrollY = 0f;
		ClearSelection();
	}

	public void ClearSelection() {
		_anchor = _focus = -1;
		Repaint();
	}

	public void SelectAll() {
		int total = Channel.GetCount();

		if (total == 0) return;

		_anchor = 0;
		_focus = total - 1;
		Repaint();
	}

	public string Copy() => _anchor < 0 || _focus < 0 ? Channel.ReadAll() : Channel.ReadRange(Math.Min(_anchor, _focus), Math.Max(_anchor, _focus));

	private int LineAt(float y) {
		int line = _firstLine + (int)(y / _lineHeight);
		return line >= 0 && line < Channel.GetCount() ? line : -1;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		RequestFocus();

		int line = LineAt(y);
		_anchor = _focus = line;

		if (line >= 0) Ui?.SetMouseCapture(this);

		Repaint();
		return true;
	}

	public override void OnCursorMoved(float x, float y) {
		if (_anchor < 0 || !ReferenceEquals(Ui?.GetMouseCapture(), this)) return;

		int line = LineAt(Math.Clamp(y, 0f, Tall - 1f));

		if (line < 0 || line == _focus) return;

		_focus = line;
		Repaint();
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if ((modifiers & KeyModifiers.Control) == 0) return false;

		if (key == VirtualKey.C) {
			Clipboard.SetText(Copy());
			return true;
		}

		if (key != VirtualKey.A) return false;

		SelectAll();
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float maximum = Math.Max(0f, (Channel.GetCount() - _visible) * _lineHeight);
		_scrollY = Math.Clamp(_scrollY + (delta / 120f * 3f * _lineHeight), 0f, maximum);
		Repaint();
		return true;
	}

	public override void Paint(IPainter painter) {
		ITextFont font = painter.GetCode();
		float scale = Scale;
		float rowHeight = font.GetLineHeight();
		IOutputChannel channel = Channel;
		int total = channel.GetCount();

		_lineHeight = rowHeight;
		_visible = Math.Max(1, (int)(Tall / rowHeight));

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		if (total == 0) {
			_firstLine = 0;
			ITextFont ui = painter.GetUi();
			painter.DrawText(ui, $"No output yet from {channel.GetName()}.", ScreenX + (PaddingX * scale), ScreenY + (Metrics.Space12 * scale) + ui.GetBaseline(), Theme.SectionHeaderForeground);
			painter.PopClip();
			return;
		}

		int maxSkip = Math.Max(0, total - _visible);
		_scrollY = Math.Min(_scrollY, maxSkip * rowHeight);
		int skip = Math.Clamp(maxSkip - (int)Math.Round(_scrollY / rowHeight), 0, maxSkip);
		int count = channel.CopyTo(_lines, skip);
		int first = Math.Min(_anchor, _focus);
		int last = Math.Max(_anchor, _focus);

		_firstLine = skip;

		for (int i = 0; i < count && i <= _visible; i++) {
			float top = ScreenY + (i * rowHeight);
			int line = skip + i;

			if (_anchor >= 0 && line >= first && line <= last) painter.FillRectangle(ScreenX, top, Wide, rowHeight, Theme.SelectionBackground);

			painter.DrawText(font, _lines[i], ScreenX + (PaddingX * scale), top + font.GetBaseline(), Theme.EditorForeground);
		}

		painter.PopClip();
	}
}
