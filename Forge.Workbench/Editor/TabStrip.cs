using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.Common.Editor;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class TabStrip : Panel
{
	private readonly List<EditorTab> _tabs = [];

	public TabStrip() {
		Name = "TabStrip";
		Dock = DockType.Top;
		MouseInputEnabled = true;
	}

	public const float RowHeight = 32f;

	public static float Height(float scale) => (RowHeight + 1f) * scale;

	public Action<int>? Activated { get; set; }

	public Action<int>? Closed { get; set; }

	public Action<int>? Pinned { get; set; }

	public Action<int, float, float>? ContextMenu { get; set; }

	public void Sync(IReadOnlyList<IDocument> documents, int activeIndex, Func<int, Codicon?>? iconOf = null, int previewIndex = -1) {
		while (_tabs.Count < documents.Count) {
			EditorTab tab = new(this);
			_tabs.Add(tab);
			AddChild(tab);
		}

		while (_tabs.Count > documents.Count) {
			RemoveChild(_tabs[^1]);
			_tabs.RemoveAt(_tabs.Count - 1);
			InvalidateLayout();
		}

		for (int i = 0; i < documents.Count; i++) _tabs[i].Bind(documents[i], i, i == activeIndex, iconOf?.Invoke(i), i == previewIndex);

		if (activeIndex == _active) return;

		_active = activeIndex;
		_reveal = true;
		InvalidateLayout();
	}

	private const float ScrollbarHeight = 3f;
	private const float WheelStep = 40f;

	private int _active = -1;
	private bool _reveal;
	private float _scrollX;
	private float _contentWidth;
	private bool _hovered;

	private const float ActionSize = 22f;
	private const float ActionGap = 2f;
	private const float ActionPadding = 6f;

	private readonly ActionBar _actionBar = new() { Name = "EditorActions" };
	private readonly List<ActionButton> _actions = [];

	private float TabsWidth => Math.Max(0f, Wide - _actionBar.Wide);

	private float MaxScroll => Math.Max(0f, _contentWidth - TabsWidth);

	public void SetActions(IReadOnlyList<(string Command, string Title, Codicon Icon)> actions) {
		bool same = actions.Count == _actions.Count;

		for (int i = 0; same && i < actions.Count; i++) same = _actions[i].Command == actions[i].Command && _actions[i].Icon == actions[i].Icon;

		if (same) return;

		foreach (ActionButton existing in _actions) _actionBar.RemoveChild(existing);

		_actions.Clear();

		foreach ((string command, string title, Codicon icon) in actions) {
			ActionButton button = new() { Name = title, Command = command, Icon = icon, Foreground = Theme.ChromeForeground, HoverBackground = Theme.ListHoverBackground, Radius = Metrics.RadiusSmall };
			_actions.Add(button);
			_actionBar.AddChild(button);
		}

		InvalidateLayout();
		Repaint();
	}

	private sealed class ActionBar : Panel
	{
		public override void PaintBackground(IPainter painter) => painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.TabsBackground);
	}

	public override void PerformLayout() {
		float scale = Scale;
		float height = RowHeight * scale;
		float total = 0f;

		foreach (EditorTab tab in _tabs) {
			tab.Dock = DockType.None;
			tab.SetSize(tab.PreferredWidth(scale), height);
			total += tab.Wide;
		}

		_contentWidth = total;

		float action = ActionSize * scale;
		float barWidth = _actions.Count == 0 ? 0f : (_actions.Count * (action + (ActionGap * scale))) + (ActionPadding * 2f * scale);

		if (_actionBar.Parent is null) AddChild(_actionBar);

		_actionBar.MoveToFront();
		_actionBar.Visible = _actions.Count > 0;
		_actionBar.SetBounds(Wide - barWidth, 0f, barWidth, height);

		float actionX = ActionPadding * scale;

		foreach (ActionButton button in _actions) {
			button.SetBounds(actionX, (height - action) / 2f, action, action);
			actionX += action + (ActionGap * scale);
		}

		if (_reveal && (uint)_active < (uint)_tabs.Count) {
			_reveal = false;
			float start = 0f;

			for (int i = 0; i < _active; i++) start += _tabs[i].Wide;

			float end = start + _tabs[_active].Wide;

			if (start < _scrollX) _scrollX = start;
			else if (end > _scrollX + TabsWidth) _scrollX = end - TabsWidth;
		}

		_scrollX = Math.Clamp(_scrollX, 0f, MaxScroll);

		float x = -MathF.Round(_scrollX);

		foreach (EditorTab tab in _tabs) {
			tab.SetPos(x, 0f);
			x += tab.Wide;
		}
	}

	public override void OnSizeChanged(float wide, float tall) {
		_reveal = true;
		InvalidateLayout();
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		if (MaxScroll <= 0f) return true;

		float previous = _scrollX;
		_scrollX = Math.Clamp(_scrollX - (delta / 120f * WheelStep * Scale), 0f, MaxScroll);

		if (_scrollX != previous) {
			InvalidateLayout();
			Repaint();
		}

		return true;
	}

	public override void OnCursorEntered() {
		_hovered = true;
		Repaint();
	}

	public override void OnCursorExited() {
		_hovered = false;
		Repaint();
	}

	public override void PaintBackground(IPainter painter) {
		painter.PushClip(ScreenX, ScreenY, Wide, Tall);
		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.TabsBackground);
		painter.FillRectangle(ScreenX, ScreenY + Tall - Scale, Wide, Scale, Theme.TabsBorder);
	}

	public override void PostChildPaint(IPainter painter) {
		if ((_hovered || _tabs.Exists(static tab => tab.IsCursorOver)) && MaxScroll > 0f) {
			float bar = ScrollbarHeight * Scale;
			float track = TabsWidth;
			float thumb = Math.Max(bar * 4f, track * track / _contentWidth);
			float thumbX = ScreenX + ((track - thumb) * (_scrollX / MaxScroll));
			painter.FillRectangle(thumbX, ScreenY + Tall - bar, thumb, bar, Theme.ScrollbarSlider);
		}

		painter.PopClip();
	}
}
