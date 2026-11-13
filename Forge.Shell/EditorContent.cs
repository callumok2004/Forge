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
		workbench.Minimap.RequestTick = () => SetThinking(true);
	}

	public Action? Typed { get; set; }

	public override bool OnKeyTyped(char character) {
		if (_workbench.ActiveView is not { } view) return false;

		if (InOriginal) return true;

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
		return view.OffsetFromPoint(Code, absX, absY, _workbench.EditorLeft + DiffOffset + _workbench.TextInset, _workbench.EditorTop);
	}

	private float DiffOffset => _workbench.ActiveDiff is { } diff && !diff.OriginalFocused ? diff.ModifiedLeft : 0f;

	private float GutterOffset => DiffOffset + (_workbench.ActiveDiff is null ? _workbench.TextInset : 0f);

	private bool _minimapDragging;

	private TextView? Target => _workbench.FocusedView;

	private bool InOriginal => _workbench.ActiveDiff is { OriginalFocused: true };

	private TextView? _scrollbarView;

	private IEnumerable<TextView> ScrollbarViews() {
		if (_workbench.ActiveDiff is { } diff) {
			yield return diff.Original;
			yield return diff.Modified;
		}
		else if (_workbench.ActiveView is { } view) yield return view;
	}

	public override void OnCursorMoved(float x, float y) {
		if (_workbench.ActiveView is not { } view) return;

		(float barX, float barY) = Absolute(x, y);
		Minimap minimap = _workbench.Minimap;

		if (_minimapDragging) {
			minimap.Drag(view, barX, barY, Scale);
			Changed?.Invoke();
			Repaint();
			return;
		}

		if (_workbench.ActiveDiff is null && minimap.Hover(barX, barY, Tall, Scale)) {
			SetThinking(true);
			Repaint();
		}

		if (_workbench.ActiveDiff is null && !_selecting && minimap.Contains(barX, barY, Tall)) {
			Cursor = CursorCode.Arrow;
			return;
		}

		if (_scrollbarView is { } dragging) {
			dragging.DragScrollbar(barX, barY);

			if (_workbench.ActiveDiff is { } synced && ReferenceEquals(dragging, synced.Original)) synced.Modified.SetScrollY(dragging.ScrollY);

			Changed?.Invoke();
			Repaint();
			return;
		}

		bool overScrollbar = false;

		foreach (TextView candidate in ScrollbarViews()) {
			if (candidate.HoverScrollbar(barX, barY)) Repaint();

			overScrollbar |= candidate.ScrollbarHovered != TextView.ScrollbarPart.None;
		}

		if (overScrollbar && !_selecting) {
			Cursor = CursorCode.Arrow;
			return;
		}

		if (_workbench.ActiveDiff is { } diff && !_selecting) {
			(float diffX, float diffY) = Absolute(x, y);

			if (diff.SashDragging) {
				diff.DragSash(diffX);
				Cursor = CursorCode.SizeWestEast;
				Repaint();
				return;
			}

			if (diff.Hover(diffX, diffY)) Repaint();

			if (diff.Cursor is { } cursor) {
				Cursor = cursor;
				return;
			}

			if (!diff.IsInModified(diffX) && !diff.IsInOriginal(diffX)) {
				Cursor = CursorCode.Arrow;
				return;
			}

			if (diff.IsInOriginal(diffX)) {
				Cursor = CursorCode.IBeam;
				return;
			}
		}

		float localX = x - GutterOffset;
		bool inGlyphMargin = localX >= 0f && localX < TextView.GlyphMarginWidth;
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
			view = Target ?? view;

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
		if (!_minimapDragging) {
			_workbench.Minimap.Leave();
			SetThinking(true);
			Repaint();
		}

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

		(float barX, float barY) = Absolute(x, y);

		if (_workbench.ActiveDiff is null && _workbench.Minimap.Contains(barX, barY, Tall)) {
			if (_workbench.Minimap.Press(view, barX, barY, Scale)) {
				_minimapDragging = _workbench.Minimap.IsDragging;

				if (_minimapDragging) Ui?.SetMouseCapture(this);

				SetThinking(true);
				Changed?.Invoke();
				Repaint();
			}

			return true;
		}

		foreach (TextView candidate in ScrollbarViews()) {
			if (!candidate.BeginScrollbarDrag(barX, barY)) continue;

			_scrollbarView = candidate;

			if (_workbench.ActiveDiff is { } synced && ReferenceEquals(candidate, synced.Original)) synced.Modified.SetScrollY(candidate.ScrollY);

			Ui?.SetMouseCapture(this);
			Changed?.Invoke();
			Repaint();
			return true;
		}

		if (_workbench.ActiveDiff is { } diff) {
			(float diffX, float diffY) = Absolute(x, y);

			if (diff.IsOnSash(diffX, diffY)) {
				diff.SashDragging = true;
				Ui?.SetMouseCapture(this);
				Repaint();
				return true;
			}

			if (diff.Press(Code, diffX, diffY, clicks, Tall)) {
				Repaint();
				Changed?.Invoke();
				return true;
			}

			if (diff.OriginalFocused) {
				TextView original = diff.Original;
				int at = OffsetAt(original, x, y);
				_selecting = true;
				_dragX = x;
				_dragY = y;
				Ui?.SetMouseCapture(this);

				if (x < original.GutterWidth(Code) || clicks >= 3) original.SelectLineAt(at);
				else if (clicks == 2) original.SelectWordAt(at);
				else original.MoveTo(at, extend: (Ui!.GetModifiers() & KeyModifiers.Shift) != 0);

				Repaint();
				Pressed?.Invoke();
				return true;
			}
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

		if (x - GutterOffset < TextView.GlyphMarginWidth) {
			BreakpointToggled?.Invoke(view.Snapshot.GetLineAt(offset) + 1);
			Pressed?.Invoke();
			return true;
		}

		view.PushUndoStop();
		_selecting = true;
		_dragX = x;
		_dragY = y;
		Ui?.SetMouseCapture(this);

		if (x - GutterOffset < view.GutterWidth(Code) || clicks >= 3) view.SelectLineAt(offset);
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
		if (_minimapDragging) {
			_minimapDragging = false;
			_workbench.Minimap.EndDrag();
			SetThinking(true);
			Repaint();

			if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);
		}

		if (_scrollbarView is { } dragging) {
			dragging.EndScrollbarDrag();
			_scrollbarView = null;
			Repaint();

			if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);
		}

		if (_workbench.ActiveDiff is { SashDragging: true } diff) {
			diff.SashDragging = false;
			Repaint();

			if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);
		}

		if (!_selecting) return;

		_selecting = false;
		SetThinking(false);

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);
	}

	public override void OnThink(float deltaSeconds) {
		if (_linkActive && !LinkGesture) _workbench.IntelliSense?.Navigation.Clear();

		Minimap minimap = _workbench.Minimap;
		bool minimapTicking = minimap.WantsTick;

		if (minimapTicking) {
			minimap.Tick(deltaSeconds);
			Repaint();
		}

		if (!_selecting || Target is not { } view) {
			if (!_linkActive && !minimapTicking) SetThinking(false);

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

		if ((modifiers & KeyModifiers.Shift) != 0) {
			TextView horizontal = _workbench.ActiveDiff is { } diff && diff.IsInOriginal(Absolute(x, y).Item1) ? diff.Original : view;
			horizontal.ScrollByHorizontalWheel(-delta, font.GetCellWidth());
		}
		else view.ScrollByWheel(delta, font.GetLineHeight(), Tall);

		if (_workbench.Minimap.WantsTick) SetThinking(true);

		Changed?.Invoke();
		return true;
	}

	private float TextWidth => _workbench.TextAreaWidth(Wide);

	private static float Overshoot(float value, float low, float high) {
		if (value < low) return -Math.Min(low - value, MaximumOvershoot);

		return value > high ? Math.Min(value - high, MaximumOvershoot) : 0f;
	}

	public override void Paint(IPainter painter) => _workbench.RenderEditor(painter, ScreenX, ScreenY, Wide, Tall);
}
