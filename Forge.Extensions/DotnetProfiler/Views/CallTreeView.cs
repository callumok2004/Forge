using System.Globalization;

using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class CallTreeView : Panel
{
	private const float RowHeight = 21f;
	private const float Indent = 14f;

	private readonly HashSet<int> _expanded = [];
	private readonly List<(int Node, int Depth)> _rows = [];

	private ProfileTree _tree = ProfileTree.Empty;
	private float _scroll;
	private int _hover = -1;
	private bool _stale = true;

	public CallTreeView() => Name = "CallTree";

	public Action<string>? OpenFrame { get; set; }

	public void SetData(ProfileTree tree) {
		_tree = tree;
		_expanded.Clear();
		_scroll = 0f;
		Invalidate();
	}

	public void CollapseAll() {
		_expanded.Clear();
		Invalidate();
	}

	public void ExpandHotPath() {
		_expanded.Clear();

		for (int node = _tree.FirstChild[0]; node >= 0; node = _tree.FirstChild[node]) _expanded.Add(node);

		Invalidate();
	}

	private void Invalidate() {
		_stale = true;
		Repaint();
	}

	private void EnsureRows() {
		if (!_stale) return;

		_stale = false;
		_rows.Clear();
		Add(0, -1);
	}

	private void Add(int node, int depth) {
		if (node != 0) {
			_rows.Add((node, depth));

			if (!_expanded.Contains(node)) return;
		}

		for (int child = _tree.FirstChild[node]; child >= 0; child = _tree.NextSibling[child]) Add(child, depth + 1);
	}

	private int RowAt(float y) {
		int index = (int)((y + _scroll) / (RowHeight * Scale));
		return (uint)index < (uint)_rows.Count ? index : -1;
	}

	public override void OnCursorMoved(float x, float y) {
		int hover = RowAt(y);

		if (hover == _hover) return;

		_hover = hover;
		Repaint();
	}

	public override void OnCursorExited() {
		_hover = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		EnsureRows();

		if (button != MouseButton.Left || RowAt(y) is not (var index and >= 0)) return false;

		int node = _rows[index].Node;

		if ((Ui?.GetModifiers() & KeyModifiers.Alt) != 0) {
			OpenFrame?.Invoke(_tree.Name(node));
			return true;
		}

		if (_tree.FirstChild[node] < 0) return true;

		if (!_expanded.Remove(node)) _expanded.Add(node);

		Invalidate();
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		EnsureRows();

		float next = Math.Clamp(_scroll - (delta / 120f * RowHeight * 3f * Scale), 0f, MathF.Max(0f, (_rows.Count * RowHeight * Scale) - Tall));

		if (next == _scroll) return false;

		_scroll = next;
		Repaint();
		return true;
	}

	private static readonly Color BarColor = Color.Parse("#569cd6").WithAlpha(0.16f);

	public override void Paint(IPainter painter) {
		EnsureRows();

		float scale = Scale;
		float row = RowHeight * scale;
		ITextFont mono = painter.GetMono(12f);
		ITextFont ui = painter.GetUi(12f);
		ITextFont small = painter.GetUi(11f);
		double total = Math.Max(1, _tree.RootTotal);
		float moduleWidth = 220f * scale;
		float moduleX = ScreenX + Wide - moduleWidth;

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		if (_rows.Count == 0) {
			string message = "Record CPU to see the call tree.";
			painter.DrawText(ui, message, ScreenX + ((Wide - ui.Measure(message)) / 2f), ScreenY + (Tall / 2f), Theme.DescriptionForeground);
		}

		int first = Math.Max(0, (int)(_scroll / row));

		for (int i = first; i < _rows.Count; i++) {
			float top = ScreenY + (i * row) - _scroll;

			if (top > ScreenY + Tall) break;

			if (!painter.NeedsPaint(ScreenX, top, Wide, row)) continue;

			(int node, int depth) = _rows[i];
			double share = _tree.Total[node] / total;
			double selfShare = _tree.Self[node] / total;
			float baseline = top + ((row - ui.GetLineHeight()) / 2f) + ui.GetBaseline();
			float left = ScreenX + (((depth * 14f) + 6f) * scale);

			if (i == _hover) painter.FillRectangle(ScreenX, top, Wide, row, Theme.ListHoverBackground);

			painter.FillRectangle(ScreenX, top, (float)(Wide * share), row, BarColor);
			painter.DrawText(ui, (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%", left, baseline, Theme.EditorForeground.WithAlpha(0.9f));
			painter.DrawText(ui, (selfShare * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%", left + (60f * scale), baseline, Theme.EditorForeground.WithAlpha(0.55f));

			float nameX = left + (112f * scale);

			if (_tree.FirstChild[node] >= 0) painter.DrawChevron(nameX + (4f * scale), top + (row / 2f), 7f * scale, _expanded.Contains(node), Theme.EditorForeground.WithAlpha(0.7f));

			string name = _tree.Name(node);
			painter.PushClip(nameX + (12f * scale), top, MathF.Max(0f, moduleX - nameX - (16f * scale)), row);
			painter.DrawText(mono, name, nameX + (12f * scale), top + ((row - mono.GetLineHeight()) / 2f) + mono.GetBaseline(), Theme.EditorForeground);
			painter.PopClip();

			painter.PushClip(moduleX, top, moduleWidth, row);
			painter.DrawText(small, _tree.Module(node), moduleX, top + ((row - small.GetLineHeight()) / 2f) + small.GetBaseline(), Theme.EditorForeground.WithAlpha(0.5f));
			painter.PopClip();
		}

		painter.PopClip();
	}
}
