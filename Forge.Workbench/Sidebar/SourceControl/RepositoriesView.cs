using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;

namespace Forge.Workbench;

public sealed class RepositoriesView : Panel
{
	private const float RowHeight = 22f;

	private readonly SourceControlView _view;
	private readonly List<RepositoryHeader> _rows = [];
	private readonly List<IGitRepository> _order = [];
	private int _anchor = -1;
	private float _scrollY;

	public RepositoriesView(SourceControlView view) {
		_view = view;
		Name = "ScmRepositories";
		KeyboardInputEnabled = true;
		view.VisibilityChanged += Rebuild;
		view.RepositoriesChanged += Rebuild;
	}

	public void Rebuild() {
		IReadOnlyList<IGitRepository> all = _view.Ordered();
		bool same = all.Count == _order.Count && all.Select((repository, index) => ReferenceEquals(repository, _order[index])).All(static same => same);

		if (!same) {
			foreach (RepositoryHeader row in _rows) RemoveChild(row);

			_rows.Clear();
			_order.Clear();
			_order.AddRange(all);

			foreach (IGitRepository repository in _order) {
				IGitRepository target = repository;
				RepositoryHeader row = new(_view, repository, null, modifiers => Select(target, modifiers));
				_rows.Add(row);
				AddChild(row);
			}
		}

		for (int i = 0; i < _rows.Count; i++) {
			_rows[i].Selected = _view.IsVisible(_order[i]);
			_rows[i].Sync(_order[i]);
		}

		InvalidateLayout();
		Repaint();
	}

	private void Select(IGitRepository repository, KeyModifiers modifiers) {
		RequestFocus();

		int index = _order.IndexOf(repository);

		if (index < 0) return;

		bool single = _view.SelectionMode == "single";

		if (!single && (modifiers & KeyModifiers.Control) != 0) {
			_anchor = index;

			if (_view.IsVisible(repository) && _view.VisibleRoots.Count == 1) return;

			_view.ToggleVisibility(repository);
			return;
		}

		if (!single && (modifiers & KeyModifiers.Shift) != 0 && _anchor >= 0 && _anchor < _order.Count) {
			int from = Math.Min(_anchor, index);
			int to = Math.Max(_anchor, index);
			_view.SetVisible([.. _order.Skip(from).Take(to - from + 1)]);
			return;
		}

		_anchor = index;
		_view.SetVisible([repository]);
	}

	public override void OnSetFocus() {
		foreach (RepositoryHeader row in _rows) row.Focused = true;

		Repaint();
	}

	public override void OnKillFocus(Panel? newFocus) {
		foreach (RepositoryHeader row in _rows) row.Focused = false;

		Repaint();
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float previous = _scrollY;
		_scrollY = Math.Clamp(_scrollY - (delta / 120f * 3f * RowHeight * Scale), 0f, Math.Max(0f, (_rows.Count * RowHeight * Scale) - Tall));

		if (_scrollY != previous) InvalidateLayout();

		return true;
	}

	public override void PerformLayout() {
		float height = RowHeight * Scale;
		_scrollY = Math.Clamp(_scrollY, 0f, Math.Max(0f, (_rows.Count * height) - Tall));

		for (int i = 0; i < _rows.Count; i++) _rows[i].SetBounds(0f, (i * height) - _scrollY, Wide, height);
	}

	public override void PaintBackground(IPainter painter) => painter.PushClip(ScreenX, ScreenY, Wide, Tall);

	public override void PostChildPaint(IPainter painter) => painter.PopClip();
}
