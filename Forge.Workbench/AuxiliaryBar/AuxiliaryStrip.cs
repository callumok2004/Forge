using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.VSCode;

namespace Forge.Workbench;

public sealed class AuxiliaryStrip : Panel
{
	private const float ActionHeight = 34f;
	private const float ActionGap = 4f;
	private const float IndicatorRadius = 9f;
	private const float IconSize = 18f;

	private readonly AuxiliaryBar _bar;
	private readonly Dictionary<string, IVectorIcon?> _icons = new(StringComparer.Ordinal);
	private readonly List<(ActionButton Button, string ViewId)> _buttons = [];

	public AuxiliaryStrip(AuxiliaryBar bar) {
		_bar = bar;
		Name = "AuxiliaryStrip";
		MouseInputEnabled = false;
	}

	public Func<string, IVectorIcon?>? IconLoader { get; set; }

	public static float Width(float scale) => (Metrics.ActivityBarWidth + Metrics.CardMargin) * scale;

	public void Rebuild() {
		foreach ((ActionButton button, _) in _buttons) RemoveChild(button);

		_buttons.Clear();

		foreach (ViewPaneContainer container in _bar.Containers) {
			string id = container.Container.Id;
			ContainerButton button = new(id) {
				Name = container.Container.Title,
				Icon = container.Container.Icon,
				VectorIcon = container.Container.VectorIcon,
				IconSize = IconSize,
				Dock = DockType.Top,
				Foreground = Theme.ActivityBarForeground,
				HoverForeground = Theme.Foreground,
				HoverBackground = Color.Transparent,
				CheckedRadius = IndicatorRadius,
				Movable = container.Container.Movable,
				Action = () => _bar.Activate(id),
			};

			_buttons.Add((button, id));
			AddChild(button);
		}

		foreach (WebviewViewEntry view in _bar.Views) {
			string viewId = view.ViewId;
			ContainerButton button = new(viewId) {
				Name = viewId,
				Icon = Codicon.Extensions,
				VectorIcon = IconFor(view),
				IconSize = IconSize,
				Dock = DockType.Top,
				Foreground = Theme.ActivityBarForeground,
				HoverForeground = Theme.Foreground,
				HoverBackground = Color.Transparent,
				CheckedRadius = IndicatorRadius,
				Action = () => _bar.Activate(viewId),
			};

			_buttons.Add((button, viewId));
			AddChild(button);
		}

		List<string> natural = [.. _buttons.Select(static entry => entry.ViewId)];
		_buttons.Sort((left, right) => Rank(left.ViewId, natural).CompareTo(Rank(right.ViewId, natural)));

		foreach ((ActionButton button, _) in _buttons) RemoveChild(button);

		foreach ((ActionButton button, _) in _buttons) AddChild(button);

		Sync();
		InvalidateLayout();
	}

	private readonly List<string> _customOrder = [];

	public Action<IReadOnlyList<string>>? OrderChanged { get; set; }

	public void SetCustomOrder(IEnumerable<string>? order) {
		_customOrder.Clear();

		if (order is not null) _customOrder.AddRange(order);

		Rebuild();
	}

	private int Rank(string id, List<string> natural) {
		int index = _customOrder.IndexOf(id);
		return index >= 0 ? index : 10000 + natural.IndexOf(id);
	}

	public bool Contains(string id) => _buttons.Exists(entry => entry.ViewId == id);

	public bool ContainsPoint(float absoluteX, float absoluteY) {
		(float x, float y) = GetAbsPos();
		return Visible && absoluteX >= x && absoluteX < x + Wide && absoluteY >= y && absoluteY < y + Tall;
	}

	public int? DropIndex {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public int DropIndexAt(float absoluteY) {
		for (int i = 0; i < _buttons.Count; i++) {
			ActionButton button = _buttons[i].Button;
			(_, float top) = button.GetAbsPos();

			if (absoluteY < top + (button.Tall / 2f)) return i;
		}

		return _buttons.Count;
	}

	public void MoveEntry(string id, int index) {
		int current = _buttons.FindIndex(entry => entry.ViewId == id);

		if (current < 0) return;

		List<string> order = [.. _buttons.Select(static entry => entry.ViewId)];
		order.RemoveAt(current);
		order.Insert(Math.Clamp(current < index ? index - 1 : index, 0, order.Count), id);

		_customOrder.Clear();
		_customOrder.AddRange(order);
		Rebuild();
		OrderChanged?.Invoke(_customOrder);
	}

	public override void PostChildPaint(IPainter painter) {
		base.PostChildPaint(painter);

		if (DropIndex is not { } index || _buttons.Count == 0) return;

		float scale = Scale;
		float thickness = 2f * scale;
		float gap = ActionGap * scale;
		ActionButton reference = _buttons[Math.Min(index, _buttons.Count - 1)].Button;
		float y = index < _buttons.Count ? reference.ScreenY - (gap / 2f) : reference.ScreenY + reference.Tall + (gap / 2f);

		painter.FillRectangle(reference.ScreenX, y - (thickness / 2f), reference.Wide, thickness, Theme.ActivityBarForeground);
	}

	public void SetBadge(string id, int count) {
		foreach ((ActionButton button, string viewId) in _buttons) {
			if (viewId == id) button.BadgeCount = count;
		}
	}

	public void Sync() {
		foreach ((ActionButton button, string viewId) in _buttons) button.Checked = _bar.IsOpen && string.Equals(viewId, _bar.ActiveViewId, StringComparison.Ordinal);
	}

	private IVectorIcon? IconFor(WebviewViewEntry view) {
		if (view.IconPath is not { } path || IconLoader is not { } loader) return null;

		if (!_icons.TryGetValue(path, out IVectorIcon? icon)) _icons[path] = icon = loader(path);

		return icon;
	}

	public override void PerformLayout() {
		float scale = Scale;

		DockPadding = new Thickness(0f, 0f, Metrics.CardMargin * scale, 0f);

		foreach ((ActionButton button, _) in _buttons) {
			button.SetSize(Metrics.ActivityBarWidth * scale, ActionHeight * scale);
			button.DockMargin = new Thickness(0f, 0f, 0f, ActionGap * scale);
		}

		base.PerformLayout();
	}
}
