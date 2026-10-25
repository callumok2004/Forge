using System.Diagnostics;

using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core;
using Forge.Core.Native;

namespace Forge.UI.Framework;

public sealed class UiSystem : IUiSystem
{
	private const long MultiClickMilliseconds = 500;
	private const float MultiClickSlop = 4f;

	private readonly List<(Panel Panel, bool Modal)> _popups = [];
	private readonly List<Panel> _thinking = [];
	private readonly List<(Panel Panel, long Interval, long Next)> _ticks = [];

	private Panel? _focus;
	private Panel? _mouseOver;
	private Panel? _capture;
	private Panel? _pressed;

	private long _lastClickAt;
	private float _lastClickX;
	private float _lastClickY;
	private int _clickCount;
	private float _cursorX;
	private float _cursorY;

	public UiSystem(Action<PixelRect> repaint, Action requestFrame, Func<float> scale, Func<KeyModifiers> modifiers) {
		RepaintSink = repaint;
		RequestFrame = requestFrame;
		ScaleSource = scale;
		ModifierSource = modifiers;
		Root = new Panel { Name = "Root" };
		Panel.Attach(this);
	}

	private Action<PixelRect> RepaintSink { get; }

	private Action RequestFrame { get; }

	private Func<float> ScaleSource { get; }

	private Func<KeyModifiers> ModifierSource { get; }

	public Panel Root { get; }

	public Func<string, Panel?, bool>? CommandHandler { get; set; }

	public Func<uint, KeyModifiers, bool>? KeyFallback { get; set; }

	public Action<CursorCode>? CursorChanged { get; set; }

	public bool IsThinking => _thinking.Count > 0 || _ticks.Count > 0;

	public float GetScale() => ScaleSource();

	public float GetViewportWidth() => Root.Wide;

	public float GetViewportHeight() => Root.Tall;

	public KeyModifiers GetModifiers() => ModifierSource();

	public void Repaint(PixelRect area) {
		if (!area.IsEmpty) RepaintSink(area);
	}

	public void InvalidateLayout(Panel panel) => RequestFrame();

	#region Debugging

	private const float FlashSeconds = 0.3f;

	private readonly List<(Panel Panel, long Expires)> _flashes = [];

	public bool VisualizeLayout { get; set; }

	public bool VisualizePaint { get; set; }

	private readonly List<(PixelRect Area, long Expires)> _paints = [];
	private bool _fading;

	public void NotePaint(PixelRect area) {
		if (!VisualizePaint || _fading || area.IsEmpty) return;

		_paints.Add((area, Stopwatch.GetTimestamp() + (long)(FlashSeconds * Stopwatch.Frequency)));

		if (_paints.Count > 256) _paints.RemoveAt(0);
	}

	public Panel? Highlighted {
		get;
		set {
			if (ReferenceEquals(field, value)) return;

			field?.Repaint();

			field = value;

			value?.Repaint();
		}
	}

	public void LayoutPerformed(Panel panel) {
		if (!VisualizeLayout) return;

		long expires = Stopwatch.GetTimestamp() + (long)(FlashSeconds * Stopwatch.Frequency);
		int index = _flashes.FindIndex(entry => ReferenceEquals(entry.Panel, panel));

		if (index >= 0) _flashes[index] = (panel, expires);
		else _flashes.Add((panel, expires));

		panel.Repaint();
	}

	private void FadeFlashes() {
		if (_flashes.Count == 0 && _paints.Count == 0) return;

		long now = Stopwatch.GetTimestamp();
		_fading = true;

		for (int i = _flashes.Count - 1; i >= 0; i--) {
			_flashes[i].Panel.Repaint();

			if (_flashes[i].Expires <= now) _flashes.RemoveAt(i);
		}

		for (int i = _paints.Count - 1; i >= 0; i--) {
			RepaintSink(_paints[i].Area);

			if (_paints[i].Expires <= now) _paints.RemoveAt(i);
		}

		_fading = false;
	}

	private void PaintDebugOverlay(IPainter painter) {
		long now = Stopwatch.GetTimestamp();

		foreach ((Panel panel, long expires) in _flashes) {
			float alpha = Math.Clamp((float)(expires - now) / Stopwatch.Frequency / FlashSeconds, 0f, 1f) * (100f / 255f);
			(float x, float y) = panel.GetAbsPos();

			painter.FillRectangle(x, y, panel.Wide, panel.Tall, new Theming.Color(1f, 0f, 0f, alpha));
			painter.StrokeRoundedRectangle(x, y, panel.Wide, panel.Tall, 0f, 1f, new Theming.Color(0f, 1f, 0f, alpha));
		}

		foreach ((PixelRect area, long expires) in _paints) {
			float alpha = Math.Clamp((float)(expires - now) / Stopwatch.Frequency / FlashSeconds, 0f, 1f) * 0.35f;

			painter.FillRectangle(area.Left, area.Top, area.Width, area.Height, new Theming.Color(1f, 0.85f, 0f, alpha));
			painter.StrokeRoundedRectangle(area.Left + 0.5f, area.Top + 0.5f, area.Width - 1f, area.Height - 1f, 0f, 1f, new Theming.Color(1f, 0.6f, 0f, alpha * 2f));
		}

		if (Highlighted is not { } highlighted || !highlighted.IsFullyVisible()) return;

		(float hx, float hy) = highlighted.GetAbsPos();
		painter.FillRectangle(hx, hy, highlighted.Wide, highlighted.Tall, new Theming.Color(0.2f, 0.5f, 1f, 0.18f));
		painter.StrokeRoundedRectangle(hx, hy, highlighted.Wide, highlighted.Tall, 0f, 1f, new Theming.Color(0.3f, 0.6f, 1f, 1f));
	}

	#endregion

	#region Focus and context

	public Panel? GetFocus() => _focus;

	public void SetFocus(Panel? panel) {
		if (ReferenceEquals(_focus, panel)) return;

		Panel? previous = _focus;
		_focus = panel;

		previous?.OnKillFocus(panel);
		panel?.OnSetFocus();
	}

	public bool HasContext(string key) {
		for (Panel? current = _focus; current is not null; current = current.Parent)
			if (string.Equals(current.ContextKey, key, StringComparison.Ordinal)) return true;

		return false;
	}

	#endregion

	#region Mouse

	public Panel? GetMouseOver() => _mouseOver;

	public Panel? GetMouseCapture() => _capture;

	public void SetMouseCapture(Panel? panel) {
		if (ReferenceEquals(_capture, panel)) return;

		Panel? previous = _capture;
		_capture = panel;
		previous?.OnMouseCaptureLost();
	}

	public Panel? HitTest(float x, float y) {
		for (int i = _popups.Count - 1; i >= 0; i--) {
			(Panel popup, bool modal) = _popups[i];

			if (popup.IsWithinTraverse(x, y) is { } hit) return hit;

			if (modal) return null;
		}

		return Root.IsWithinTraverse(x, y);
	}

	public bool InternalCursorMoved(float x, float y) {
		_cursorX = x;
		_cursorY = y;

		Panel? target = _capture ?? HitTest(x, y);

		if (!ReferenceEquals(target, _mouseOver)) {
			Panel? previous = _mouseOver;
			_mouseOver = target;

			previous?.OnCursorExited();
			target?.OnCursorEntered();
		}

		CursorChanged?.Invoke(target?.Cursor ?? CursorCode.Arrow);

		if (target is null) return _popups.Count > 0 && _popups[^1].Modal;

		(float absX, float absY) = target.GetAbsPos();
		target.OnCursorMoved(x - absX, y - absY);
		return true;
	}

	public void InternalCursorExited() {
		Panel? previous = _mouseOver;
		_mouseOver = null;
		previous?.OnCursorExited();
	}

	public bool InternalMousePressed(MouseButton button, float x, float y) {
		if (DismissPopupsOutside(x, y)) return true;

		Panel? target = _capture ?? HitTest(x, y);

		if (target is null) return false;

		long now = Stopwatch.GetTimestamp();
		bool chained = (now - _lastClickAt) * 1000 / Stopwatch.Frequency <= MultiClickMilliseconds && MathF.Abs(x - _lastClickX) <= MultiClickSlop && MathF.Abs(y - _lastClickY) <= MultiClickSlop;

		_clickCount = chained ? _clickCount + 1 : 1;
		_lastClickAt = now;
		_lastClickX = x;
		_lastClickY = y;
		_pressed = target;

		if (target.KeyboardInputEnabled) SetFocus(target);

		(float absX, float absY) = target.GetAbsPos();
		float localX = x - absX;
		float localY = y - absY;

		return _clickCount switch {
			1 => target.OnMousePressed(button, localX, localY),
			2 => target.OnMouseDoublePressed(button, localX, localY),
			_ => target.OnMouseTriplePressed(button, localX, localY),
		};
	}

	public bool InternalMouseReleased(MouseButton button, float x, float y) {
		Panel? target = _capture ?? _pressed ?? HitTest(x, y);

		_pressed = null;

		if (target is null) return false;

		(float absX, float absY) = target.GetAbsPos();
		bool handled = target.OnMouseReleased(button, x - absX, y - absY);

		if (_capture is not null && button == MouseButton.Left) SetMouseCapture(null);

		return handled;
	}

	public bool InternalMouseWheeled(int delta, float x, float y) {
		Panel? target = HitTest(x, y);

		if (target is null) return _popups.Count > 0 && _popups[^1].Modal;

		(float absX, float absY) = target.GetAbsPos();
		return target.OnMouseWheeled(delta, x - absX, y - absY);
	}

	public void DismissPopups() => DismissPopupsOutside(float.NegativeInfinity, float.NegativeInfinity);

	private bool DismissPopupsOutside(float x, float y) {
		bool dismissed = false;

		for (int i = _popups.Count - 1; i >= 0; i--) {
			Panel popup = _popups[i].Panel;

			if (popup.IsWithin(x, y)) break;

			if (!popup.DismissOnOutsideClick) continue;

			popup.OnPopupDismissed();
			dismissed = true;
		}

		return dismissed;
	}

	#endregion

	#region Keyboard

	public bool InternalKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (_focus is { ShortcutsFirst: true } && KeyFallback?.Invoke(key, modifiers) == true) return true;

		for (Panel? current = _focus; current is not null; current = current.Parent)
			if (current.OnKeyCodePressed(key, modifiers)) return true;

		if (key == VirtualKey.Escape && _popups.Count > 0) {
			_popups[^1].Panel.OnPopupDismissed();
			return true;
		}

		return _focus is not { ShortcutsFirst: true } && (KeyFallback?.Invoke(key, modifiers) ?? false);
	}

	public bool InternalKeyTyped(char character) {
		for (Panel? current = _focus; current is not null; current = current.Parent)
			if (current.OnKeyTyped(character)) return true;

		return false;
	}

	#endregion

	#region Popups

	public bool HasPopups => _popups.Count > 0;

	public IEnumerable<Panel> Popups {
		get {
			foreach ((Panel popup, _) in _popups) yield return popup;
		}
	}

	public int CopyPopupBounds(Span<PixelRect> destination) {
		int count = Math.Min(destination.Length, _popups.Count);

		for (int i = 0; i < count; i++) destination[i] = _popups[i].Panel.AbsoluteBounds;

		return count;
	}

	private Panel? _focusBeforePopups;

	public void AddPopup(Panel popup, bool modal) {
		RemovePopup(popup);

		if (_popups.Count == 0) _focusBeforePopups = _focus;

		_popups.Add((popup, modal));

		if (popup.KeyboardInputEnabled) SetFocus(popup);
	}

	public void RemovePopup(Panel popup) {
		int index = _popups.FindIndex(entry => ReferenceEquals(entry.Panel, popup));

		if (index < 0) return;

		_popups.RemoveAt(index);

		if (_focus is not null && (ReferenceEquals(_focus, popup) || _focus.HasParent(popup))) SetFocus(_popups.Count > 0 ? _popups[^1].Panel : _focusBeforePopups);

		if (_popups.Count == 0) _focusBeforePopups = null;

		if (_mouseOver is not null && (ReferenceEquals(_mouseOver, popup) || _mouseOver.HasParent(popup))) _mouseOver = null;
	}

	#endregion

	#region Think and ticks

	public void SetThinking(Panel panel, bool thinking) {
		bool present = _thinking.Contains(panel);

		if (thinking && !present) _thinking.Add(panel);
		else if (!thinking && present) _thinking.Remove(panel);
	}

	public void AddTickSignal(Panel panel, int intervalMilliseconds) {
		RemoveTickSignal(panel);

		long interval = Math.Max(1, intervalMilliseconds) * Stopwatch.Frequency / 1000;
		_ticks.Add((panel, interval, Stopwatch.GetTimestamp() + interval));
	}

	public void RemoveTickSignal(Panel panel) => _ticks.RemoveAll(entry => ReferenceEquals(entry.Panel, panel));

	public void RunFrame(float deltaSeconds) {
		FadeFlashes();

		for (int i = _thinking.Count - 1; i >= 0; i--) _thinking[i].OnThink(deltaSeconds);

		long now = Stopwatch.GetTimestamp();

		for (int i = 0; i < _ticks.Count; i++) {
			(Panel panel, long interval, long next) = _ticks[i];

			if (now < next) continue;

			_ticks[i] = (panel, interval, now + interval);
			panel.OnTick();
		}
	}

	#endregion

	public Func<string, string?>? KeybindingLookup { get; set; }

	public Func<string, bool>? EnabledLookup { get; set; }

	public required IFontSet Fonts { get; init; }

	public string? GetKeybinding(string command) => KeybindingLookup?.Invoke(command);

	public bool IsCommandEnabled(string command) => EnabledLookup?.Invoke(command) ?? true;

	public IFontSet GetFonts() => Fonts;

	public void PostCommand(string command, Panel? from) {
		if (from?.OnCommand(command) == true) return;

		CommandHandler?.Invoke(command, from);
	}

	public void Layout(float width, float height) {
		Root.SetBounds(0f, 0f, width, height);
		Root.Solve();

		foreach ((Panel popup, _) in _popups) popup.Solve();
	}

	public void Paint(IPainter painter, PixelRect damage) {
		Root.PaintTraverse(painter, damage, 0f, 0f);

		foreach ((Panel popup, _) in _popups) popup.PaintTraverse(painter, damage, 0f, 0f);

		PaintDebugOverlay(painter);
	}
}
