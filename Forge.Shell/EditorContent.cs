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
		(float lensX, float lensY) = Absolute(x, y);
		int lens = _selecting ? -1 : view.CodeLensAt(lensX, lensY);

		if (lens != view.HoveredLens) {
			view.HoveredLens = lens;
			Repaint();
		}

		Cursor = inGlyphMargin ? CursorCode.Arrow : _linkActive || lens >= 0 ? CursorCode.Hand : CursorCode.IBeam;

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


		if (!inGlyphMargin && LinkGesture) intelliSense.LinkHover(offset);
		else intelliSense.Navigation.Clear();
		bool hoverWasOpen = intelliSense.Hover.IsOpen;
		(float absX, float absY) = Absolute(x, y);

		intelliSense.PointerMoved(line + 1, offset - view.Snapshot.GetLineStartOffset(line) + 1, absX, absY);

		if (hoverWasOpen) Changed?.Invoke();
	}

	private bool LinkGesture => ((Ui?.GetModifiers() ?? KeyModifiers.None) & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)) == KeyModifiers.Control;

	public void SyncLink() {
		if (_workbench.ActiveView is not { } view || _workbench.IntelliSense is not { } intelliSense) return;

		view.Link = intelliSense.Navigation.Current is { } link ? new TextView.LinkRange(link.Line - 1, link.StartColumn - 1, link.EndColumn - 1) : null;
		Cursor = view.Link is null ? CursorCode.IBeam : CursorCode.Hand;
		_linkActive = view.Link is not null;

		if (_linkActive) SetThinking(true);

		Repaint();
	}

	private bool _linkActive;


	public override void OnCursorExited() {
		_workbench.IntelliSense?.Navigation.Clear();
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
		(float absoluteX, float absoluteY) = Absolute(x, y);

		if (view.LightbulbBounds is { } bulb && absoluteX >= bulb.Left && absoluteX < bulb.Left + bulb.Width && absoluteY >= bulb.Top && absoluteY < bulb.Top + bulb.Height) {
			_workbench.IntelliSense?.OpenLightbulbMenu(bulb.Left, bulb.Top + bulb.Height);
			Pressed?.Invoke();
			return true;
		}

		if (view.IsInCodeLensRow(absoluteY)) {
			if (view.CodeLensAt(absoluteX, absoluteY) is >= 0 and int lens) _workbench.IntelliSense?.ExecuteCodeLens(lens);

			return true;
		}

		if (clicks == 1 && LinkGesture && _workbench.IntelliSense?.Navigation.Click(offset) == true) {
			Pressed?.Invoke();
			return true;
		}

		if (x < TextView.GlyphMarginWidth) {
			BreakpointToggled?.Invoke(view.Snapshot.GetLineAt(offset) + 1);
			Pressed?.Invoke();
			return true;
		}

		view.PushUndoStop();
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
		if (_linkActive && !LinkGesture) _workbench.IntelliSense?.Navigation.Clear();

		if (!_selecting || _workbench.ActiveView is not { } view) {
			if (!_linkActive) SetThinking(false);

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
