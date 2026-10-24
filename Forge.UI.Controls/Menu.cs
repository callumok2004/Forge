using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.UI.Controls;

public sealed class Menu : Panel
{
	private const float PaddingY = 4f;
	private const float MinimumWidth = 160f;

	private readonly List<MenuItem> _items = [];

	private Menu? _parentMenu;
	private Menu? _openChild;
	private float _anchorX;
	private float _anchorY;

	public Menu() {
		KeyboardInputEnabled = true;
		ContextKey = "menuFocus";
	}

	public MenuItem? Highlighted { get; private set; }

	public Action? Closed { get; set; }

	public bool IsOpen => IsPopup;

	public IReadOnlyList<MenuItem> Items => _items;

	public MenuItem Add(MenuItem item) {
		_items.Add(item);
		AddChild(item);
		return item;
	}

	public void AddSeparator() => Add(MenuItem.Separator());

	public void Clear() {
		foreach (MenuItem item in _items) RemoveChild(item);

		_items.Clear();
		Highlighted = null;
	}

	public void Open(float x, float y) {
		_anchorX = x;
		_anchorY = y;
		Highlighted = null;
		MakePopup();
		RequestFocus();
	}

	public void CloseAll() {
		Menu root = this;

		while (root._parentMenu is { } parent) root = parent;

		root.Close();
	}

	public void Close() {
		_openChild?.Close();
		_openChild = null;

		if (!IsPopup) return;

		ClosePopup();

		if (_parentMenu is { } parent) {
			parent._openChild = null;
			parent.RequestFocus();
		}

		_parentMenu = null;
		Closed?.Invoke();
	}

	public override void OnPopupDismissed() => Close();

	public void Highlight(MenuItem? item, bool fromKeyboard) {
		if (ReferenceEquals(Highlighted, item)) return;

		Highlighted?.Repaint();
		Highlighted = item;
		item?.Repaint();

		if (!ReferenceEquals(_openChild, item?.Submenu)) {
			_openChild?.Close();
			_openChild = null;
		}

		if (item?.Submenu is { } submenu && !fromKeyboard) OpenSubmenu(item, submenu, focus: false);
	}

	public void Activate(MenuItem item) {
		if (!item.IsEnabled || item.IsSeparator) return;

		if (item.Submenu is { } submenu) {
			OpenSubmenu(item, submenu, focus: true);
			return;
		}

		CloseAll();

		if (item.Action is { } action) action();
		else if (item.Command is { } command) PostCommand(command);
	}

	private void OpenSubmenu(MenuItem item, Menu submenu, bool focus) {
		if (ReferenceEquals(_openChild, submenu) && submenu.IsPopup) {
			if (focus) submenu.FocusFirst();

			return;
		}

		_openChild?.Close();
		_openChild = submenu;
		submenu._parentMenu = this;

		(float x, float y) = item.GetAbsPos();
		submenu.Open(x + item.Wide - (Metrics.Space2 * Scale), y - (PaddingY * Scale));

		if (focus) submenu.FocusFirst();
		else RequestFocus();
	}

	private void FocusFirst() {
		RequestFocus();
		Step(1);
	}

	private void Step(int delta) {
		if (_items.Count == 0) return;

		int index = Highlighted is null ? (delta > 0 ? -1 : _items.Count) : _items.IndexOf(Highlighted);

		for (int i = 0; i < _items.Count; i++) {
			index = ((index + delta) % _items.Count + _items.Count) % _items.Count;

			if (!_items[index].IsSeparator) {
				Highlight(_items[index], fromKeyboard: true);
				return;
			}
		}
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		switch (key) {
			case VirtualKey.Down:
				Step(1);
				return true;

			case VirtualKey.Up:
				Step(-1);
				return true;

			case VirtualKey.Right when Highlighted?.Submenu is { } submenu:
				OpenSubmenu(Highlighted, submenu, focus: true);
				return true;

			case VirtualKey.Left when _parentMenu is not null:
				Close();
				return true;

			case VirtualKey.Return when Highlighted is { } item:
				Activate(item);
				return true;

			case VirtualKey.Escape:
				Close();
				return true;
		}

		return true;
	}

	public override void PerformLayout() {
		float scale = Scale;
		ITextFont font = Ui!.GetFonts().GetUi();
		float width = MinimumWidth * scale;
		float height = PaddingY * 2f * scale;

		foreach (MenuItem item in _items) {
			width = MathF.Max(width, item.PreferredWidth(font, scale));

			float itemHeight = (item.IsSeparator ? MenuItem.SeparatorHeight : MenuItem.RowHeight) * scale;
			item.SetSize(item.Wide, itemHeight);
			height += itemHeight;
		}

		DockPadding = new Thickness(0f, PaddingY * scale, 0f, PaddingY * scale);

		float viewportWidth = Ui.GetViewportWidth();
		float x = _anchorX + width > viewportWidth && _parentMenu is { } parent ? parent.GetAbsPos().X - width + (Metrics.Space2 * scale) : MathF.Min(_anchorX, viewportWidth - width);

		SetBounds(MathF.Max(x, 0f), MathF.Min(_anchorY, MathF.Max(Ui.GetViewportHeight() - height, 0f)), width, height);
		base.PerformLayout();
	}

	public override void PaintBackground(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusLarge * scale;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.MenuBackground);
	}

	public override void PaintBorder(IPainter painter) {
		float scale = Scale;
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * scale, Metrics.StrokeThickness * scale, Theme.MenuBorder);
	}
}
