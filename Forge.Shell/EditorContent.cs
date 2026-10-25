using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Editor;

namespace Forge.Shell;

public sealed class EditorContent : Panel
{
	private const float AutoScrollSeconds = 0.03f;
	private const float MaximumOvershoot = 160f;

	private readonly Workbench _workbench;

	private bool _selecting;
	private float _dragX;
	private float _dragY;
	private float _autoScrollElapsed;

	public EditorContent(Workbench workbench) {
		_workbench = workbench;
		Name = "EditorContent";
		Dock = DockType.Fill;
		Cursor = CursorCode.IBeam;
		KeyboardInputEnabled = true;
	}

	public Action? Typed { get; set; }

	public override bool OnKeyTyped(char character) {
		if (_workbench.ActiveView is not { } view) return false;

		if (!view.TryAutoClose(character)) view.InsertText([character]);

		_workbench.IntelliSense?.Typed(character);
		Typed?.Invoke();
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) => _workbench.IntelliSense?.HandleKey(key, (modifiers & KeyModifiers.Control) != 0) ?? false;

	public Action? Changed { get; set; }

	public Action? Pressed { get; set; }

	public Action<int>? BreakpointToggled { get; set; }

	public Action<int>? ZoomRequested { get; set; }

	public Func<bool>? WheelZooms { get; set; }

	private static ITextFont Code => Ui!.GetFonts().GetCode();

	private (float X, float Y) Absolute(float x, float y) {
		(float left, float top) = GetAbsPos();
		return (left + x, top + y);
	}

	private int OffsetAt(TextView view, float x, float y) {
		(float absX, float absY) = Absolute(x, y);
		return view.OffsetFromPoint(Code, absX, absY, _workbench.EditorLeft, _workbench.EditorTop);
	}

	public override void OnCursorMoved(float x, float y) {
		if (_workbench.ActiveView is not { } view) return;

		bool inGlyphMargin = x < TextView.GlyphMarginWidth;
		Cursor = inGlyphMargin ? CursorCode.Arrow : CursorCode.IBeam;

		int gutterLine = inGlyphMargin ? view.Snapshot.GetLineAt(OffsetAt(view, x, y)) : -1;

		if (gutterLine != view.HoverGutterLine) {
			view.HoverGutterLine = gutterLine;
			Repaint(0f, 0f, TextView.GlyphMarginWidth, Tall);
		}

		if (_selecting) {
			_dragX = x;
			_dragY = y;

			int before = view.CaretOffset;
			view.MoveTo(OffsetAt(view, x, y), extend: true);
			SetThinking(Overshoot(y, 0f, Tall) != 0f || Overshoot(x, 0f, TextWidth) != 0f);

			if (view.CaretOffset != before) Changed?.Invoke();

			return;
		}

		if (_workbench.IntelliSense is not { } intelliSense) return;

		int offset = OffsetAt(view, x, y);
		int line = view.Snapshot.GetLineAt(offset);
		bool hoverWasOpen = intelliSense.Hover.IsOpen;
		(float absX, float absY) = Absolute(x, y);

		intelliSense.PointerMoved(line + 1, offset - view.Snapshot.GetLineStartOffset(line) + 1, absX, absY);

		if (hoverWasOpen) Changed?.Invoke();
	}

	public override void OnCursorExited() {
		_workbench.IntelliSense?.PointerLeft();

		if (_workbench.ActiveView is not { HoverGutterLine: >= 0 } view) return;

		view.HoverGutterLine = -1;
		Repaint(0f, 0f, TextView.GlyphMarginWidth, Tall);
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) => Press(button, x, y, 1);

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) => Press(button, x, y, 2);

	public override bool OnMouseTriplePressed(MouseButton button, float x, float y) => Press(button, x, y, 3);

	private bool Press(MouseButton button, float x, float y, int clicks) {
		_workbench.IntelliSense?.Dismiss();
		RequestFocus();

		if (button != MouseButton.Left || _workbench.ActiveView is not { } view) {
			Pressed?.Invoke();
			return true;
		}

		int offset = OffsetAt(view, x, y);

		if (x < TextView.GlyphMarginWidth) {
			BreakpointToggled?.Invoke(view.Snapshot.GetLineAt(offset) + 1);
			Pressed?.Invoke();
			return true;
		}

		_selecting = true;
		_dragX = x;
		_dragY = y;
		Ui?.SetMouseCapture(this);

		if (x < view.GutterWidth(Code) || clicks >= 3) view.SelectLineAt(offset);
		else if (clicks == 2) view.SelectWordAt(offset);
		else view.MoveTo(offset, extend: (Ui!.GetModifiers() & KeyModifiers.Shift) != 0);

		Pressed?.Invoke();
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		StopSelecting();
		return true;
	}

	public override void OnMouseCaptureLost() => StopSelecting();

	private void StopSelecting() {
		if (!_selecting) return;

		_selecting = false;
		SetThinking(false);

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);
	}

	public override void OnThink(float deltaSeconds) {
		if (!_selecting || _workbench.ActiveView is not { } view) {
			SetThinking(false);
			return;
		}

		_autoScrollElapsed += deltaSeconds;

		if (_autoScrollElapsed < AutoScrollSeconds) return;

		_autoScrollElapsed = 0f;

		ITextFont font = Code;
		float lines = Overshoot(_dragY, 0f, Tall) / font.GetLineHeight();
		float columns = Overshoot(_dragX, 0f, TextWidth) / font.GetCellWidth();

		if (lines != 0f) view.ScrollByLines(lines, font.GetLineHeight(), Tall);

		if (columns != 0f) view.ScrollByColumns(columns, font.GetCellWidth());

		view.MoveTo(OffsetAt(view, Math.Clamp(_dragX, 0f, TextWidth), Math.Clamp(_dragY, 0f, Tall - 1f)), extend: true);
		Changed?.Invoke();
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		KeyModifiers modifiers = Ui?.GetModifiers() ?? KeyModifiers.None;

		if ((modifiers & KeyModifiers.Control) != 0 && WheelZooms?.Invoke() == true) {
			ZoomRequested?.Invoke(Math.Sign(delta));
			return true;
		}

		if (_workbench.ActiveView is not { } view) return true;

		ITextFont font = Code;

		if ((modifiers & KeyModifiers.Shift) != 0) view.ScrollByHorizontalWheel(-delta, font.GetCellWidth());
		else view.ScrollByWheel(delta, font.GetLineHeight(), Tall);

		Changed?.Invoke();
		return true;
	}

	private float TextWidth => Math.Max(0f, Wide - (_workbench.ShowMinimap ? Minimap.Width(Scale) : 0f));

	private static float Overshoot(float value, float low, float high) {
		if (value < low) return -Math.Min(low - value, MaximumOvershoot);

		return value > high ? Math.Min(value - high, MaximumOvershoot) : 0f;
	}

	public override void Paint(IPainter painter) => _workbench.RenderEditor(painter, ScreenX, ScreenY, Wide, Tall);
}
