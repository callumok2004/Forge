using Forge.Common.Render;
using Forge.Core;

namespace Forge.Common.UI;

public class Panel
{
	private readonly List<Panel> _children = [];

	private DockType _dock;
	private bool _visible = true;
	private bool _needsLayout = true;

	public static IUiSystem? Ui { get; private set; }

	public static void Attach(IUiSystem system) => Ui = system;

	public Panel? Parent { get; private set; }

	public IReadOnlyList<Panel> Children => _children;

	public string? Name { get; init; }

	public float X { get; private set; }

	public float Y { get; private set; }

	public float Wide { get; private set; }

	public float Tall { get; private set; }

	public float ScreenX { get; private set; }

	public float ScreenY { get; private set; }

	public int ZPos { get; private set; }

	public bool Enabled { get; set; } = true;

	public bool MouseInputEnabled { get; set; } = true;

	public bool KeyboardInputEnabled { get; set; }

	public CursorCode Cursor { get; set; }

	public string? ContextKey { get; init; }

	public Thickness DockMargin { get; set; } = Thickness.Zero;

	public Thickness DockPadding { get; set; } = Thickness.Zero;

	public bool IsPopup { get; private set; }

	public static float Scale => Ui?.GetScale() ?? 1f;

	public bool Visible {
		get => _visible;
		set {
			if (_visible == value) return;

			_visible = value;
			Parent?.InvalidateLayout();
			RepaintArea();
		}
	}

	public DockType Dock {
		get => _dock;
		set {
			if (_dock == value) return;

			_dock = value;
			Parent?.InvalidateLayout();
		}
	}

	public bool HasFocus => ReferenceEquals(Ui?.GetFocus(), this);

	public bool IsCursorOver => ReferenceEquals(Ui?.GetMouseOver(), this);

	public bool IsLayoutInvalid => _needsLayout;

	#region Hierarchy

	public void AddChild(Panel child) {
		child.Parent?.RemoveChild(child);
		child.Parent = this;

		int index = _children.Count;

		while (index > 0 && _children[index - 1].ZPos > child.ZPos) index--;

		_children.Insert(index, child);
		OnChildAdded(child);
		InvalidateLayout();
	}

	public void RemoveChild(Panel child) {
		if (!_children.Remove(child)) return;

		child.RepaintArea();
		child.Parent = null;
		InvalidateLayout();
	}

	public void SetZPos(int z) {
		ZPos = z;

		if (Parent is not { } parent) return;

		parent._children.Remove(this);
		parent.AddChild(this);
	}

	public void MoveToFront() {
		if (Parent is not { } parent) return;

		parent._children.Remove(this);
		parent._children.Add(this);
		Repaint();
	}

	public void MoveToBack() {
		if (Parent is not { } parent) return;

		parent._children.Remove(this);
		parent._children.Insert(0, this);
		Repaint();
	}

	public bool HasParent(Panel potential) {
		for (Panel? current = Parent; current is not null; current = current.Parent)
			if (ReferenceEquals(current, potential)) return true;

		return false;
	}

	#endregion

	#region Geometry

	public void SetPos(float x, float y) => SetBounds(x, y, Wide, Tall);

	public void SetSize(float wide, float tall) => SetBounds(X, Y, wide, tall);

	public void SetBounds(float x, float y, float wide, float tall) {
		wide = MathF.Max(wide, 0f);
		tall = MathF.Max(tall, 0f);

		if (x == X && y == Y && wide == Wide && tall == Tall) return;

		bool resized = wide != Wide || tall != Tall;

		RepaintArea();

		X = x;
		Y = y;
		Wide = wide;
		Tall = tall;

		if (resized) {
			InvalidateLayout();
			OnSizeChanged(wide, tall);
		}

		RepaintArea();
	}

	public (float X, float Y) GetAbsPos() {
		float x = X;
		float y = Y;

		for (Panel? current = Parent; current is not null; current = current.Parent) {
			x += current.X;
			y += current.Y;
		}

		return (x, y);
	}

	public PixelRect AbsoluteBounds {
		get {
			(float x, float y) = GetAbsPos();
			return PixelRect.Around(x, y, Wide, Tall);
		}
	}

	public bool IsWithin(float screenX, float screenY) {
		(float x, float y) = GetAbsPos();
		return screenX >= x && screenX < x + Wide && screenY >= y && screenY < y + Tall;
	}

	public bool IsFullyVisible() {
		for (Panel? current = this; current is not null; current = current.Parent)
			if (!current._visible) return false;

		return true;
	}

	public virtual Panel? IsWithinTraverse(float screenX, float screenY) {
		if (!_visible || !IsWithin(screenX, screenY)) return null;

		for (int i = _children.Count - 1; i >= 0; i--)
			if (_children[i].IsWithinTraverse(screenX, screenY) is { } hit) return hit;

		return MouseInputEnabled ? this : null;
	}

	#endregion

	#region Layout

	public void InvalidateLayout() {
		if (_needsLayout) return;

		_needsLayout = true;
		Ui?.InvalidateLayout(this);
	}

	public void Solve() {
		if (_needsLayout) {
			_needsLayout = false;
			PerformLayout();
			Ui?.LayoutPerformed(this);
		}

		foreach (Panel child in _children)
			if (child._visible) child.Solve();
	}

	public virtual void PerformLayout() {
		float left = DockPadding.Left;
		float top = DockPadding.Top;
		float right = Wide - DockPadding.Right;
		float bottom = Tall - DockPadding.Bottom;

		foreach (Panel child in _children) {
			if (!child._visible || child._dock is DockType.None or DockType.Fill) continue;

			Thickness margin = child.DockMargin;

			switch (child._dock) {
				case DockType.Top:
					child.SetBounds(left + margin.Left, top + margin.Top, right - left - margin.Left - margin.Right, child.Tall);
					top += child.Tall + margin.Top + margin.Bottom;
					break;

				case DockType.Bottom:
					child.SetBounds(left + margin.Left, bottom - margin.Bottom - child.Tall, right - left - margin.Left - margin.Right, child.Tall);
					bottom -= child.Tall + margin.Top + margin.Bottom;
					break;

				case DockType.Left:
					child.SetBounds(left + margin.Left, top + margin.Top, child.Wide, bottom - top - margin.Top - margin.Bottom);
					left += child.Wide + margin.Left + margin.Right;
					break;

				case DockType.Right:
					child.SetBounds(right - margin.Right - child.Wide, top + margin.Top, child.Wide, bottom - top - margin.Top - margin.Bottom);
					right -= child.Wide + margin.Left + margin.Right;
					break;
			}
		}

		foreach (Panel child in _children) {
			if (!child._visible || child._dock != DockType.Fill) continue;

			Thickness margin = child.DockMargin;
			child.SetBounds(left + margin.Left, top + margin.Top, right - left - margin.Left - margin.Right, bottom - top - margin.Top - margin.Bottom);
		}
	}

	#endregion

	#region Painting

	public void Repaint() => RepaintArea();

	public void Repaint(float x, float y, float wide, float tall) {
		if (!IsFullyVisible()) return;

		(float absX, float absY) = GetAbsPos();
		Ui?.Repaint(PixelRect.Around(absX + x - Bleed, absY + y - Bleed, wide + (Bleed * 2f), tall + (Bleed * 2f)));
	}

	private const float Bleed = 2f;

	private void RepaintArea() {
		if (Wide <= 0f || Tall <= 0f || Ui is null) return;

		(float x, float y) = GetAbsPos();
		Ui.Repaint(PixelRect.Around(x - Bleed, y - Bleed, Wide + (Bleed * 2f), Tall + (Bleed * 2f)));
	}

	public void PaintTraverse(IPainter painter, PixelRect damage, float parentX, float parentY) {
		if (!_visible) return;

		ScreenX = parentX + X;
		ScreenY = parentY + Y;

		if (!damage.Intersects(ScreenX, ScreenY, Wide, Tall)) return;

		PaintBackground(painter);
		Paint(painter);

		foreach (Panel child in _children) child.PaintTraverse(painter, damage, ScreenX, ScreenY);

		PostChildPaint(painter);
		PaintBorder(painter);
	}

	public virtual void PaintBackground(IPainter painter) { }

	public virtual void Paint(IPainter painter) { }

	public virtual void PostChildPaint(IPainter painter) { }

	public virtual void PaintBorder(IPainter painter) { }

	#endregion

	#region Input

	public void RequestFocus() => Ui?.SetFocus(this);

	public virtual void OnSetFocus() => Repaint();

	public virtual void OnKillFocus(Panel? newFocus) => Repaint();

	public virtual void OnCursorEntered() { }

	public virtual void OnCursorExited() { }

	public virtual void OnCursorMoved(float x, float y) { }

	public virtual bool OnMousePressed(MouseButton button, float x, float y) => false;

	public virtual bool OnMouseDoublePressed(MouseButton button, float x, float y) => OnMousePressed(button, x, y);

	public virtual bool OnMouseTriplePressed(MouseButton button, float x, float y) => OnMouseDoublePressed(button, x, y);

	public virtual bool OnMouseReleased(MouseButton button, float x, float y) => false;

	public virtual bool OnMouseWheeled(int delta, float x, float y) => Parent?.OnMouseWheeled(delta, x + X, y + Y) ?? false;

	public virtual void OnMouseCaptureLost() { }

	public virtual bool OnKeyCodePressed(uint key, KeyModifiers modifiers) => false;

	public virtual bool OnKeyTyped(char character) => false;

	#endregion

	#region Think, ticks and commands

	public void SetThinking(bool thinking) => Ui?.SetThinking(this, thinking);

	public virtual void OnThink(float deltaSeconds) { }

	public void AddTickSignal(int intervalMilliseconds) => Ui?.AddTickSignal(this, intervalMilliseconds);

	public void RemoveTickSignal() => Ui?.RemoveTickSignal(this);

	public virtual void OnTick() { }

	public void PostCommand(string command) => Ui?.PostCommand(command, this);

	public virtual bool OnCommand(string command) => Parent?.OnCommand(command) ?? false;

	#endregion

	#region Popups

	public void MakePopup(bool modal = false) {
		IsPopup = true;
		Ui?.AddPopup(this, modal);
		InvalidateLayout();
		Repaint();
	}

	public void ClosePopup() {
		if (!IsPopup) return;

		IsPopup = false;
		Repaint();
		Ui?.RemovePopup(this);
	}

	public virtual bool DismissOnOutsideClick => true;

	public virtual void OnPopupDismissed() => ClosePopup();

	#endregion

	public virtual void OnChildAdded(Panel child) { }

	public virtual void OnSizeChanged(float wide, float tall) { }
}
