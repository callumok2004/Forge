using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class FlameGraph : Panel
{
	private const float RowHeight = 20f;
	private const float MinimumPixels = 0.4f;

	private static readonly Color LabelColor = Color.Parse("#0b0b0b");

	private readonly List<Box> _boxes = [];
	private readonly Dictionary<int, Color> _colors = [];

	private ProfileTree _tree = ProfileTree.Empty;
	private string?[] _short = [];
	private Func<long, string> _format = static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
	private int _focus;
	private int _maxDepth;
	private int _hover = -1;
	private float _mouseX;
	private float _mouseY;
	private float _scroll;
	private float _laidOutWidth;
	private string _filter = string.Empty;
	private System.Text.RegularExpressions.Regex? _pattern;

	private readonly record struct Box(int Node, int Depth, float X0, float X1);

	public FlameGraph() {
		Name = "FlameGraph";
	}

	public bool ShowSystem {
		get;
		set {
			field = value;
			Relayout();
		}
	}

	public Action<string>? OpenFrame { get; set; }

	public void SetData(ProfileTree tree, Func<long, string> format) {
		_tree = tree;
		_short = new string?[tree.Names.Length];
		_format = format;
		_focus = 0;
		_scroll = 0f;
		_colors.Clear();
		Relayout();
	}

	public void SetFilter(string filter) {
		_filter = filter;

		try {
			_pattern = filter.Length == 0 ? null : new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(5));
		}
		catch (ArgumentException) {
			_pattern = null;
		}

		Repaint();
	}

	private bool Matches(string name) {
		if (_filter.Length == 0) return true;

		try {
			return _pattern?.IsMatch(name) ?? name.Contains(_filter, StringComparison.OrdinalIgnoreCase);
		}
		catch (System.Text.RegularExpressions.RegexMatchTimeoutException) {
			return false;
		}
	}

	public void ResetZoom() {
		_focus = 0;
		Relayout();
	}

	public void FocusOnName(string name) {
		int best = -1;

		for (int node = 1; node < _tree.Count; node++) {
			if (_tree.Name(node) == name && (best < 0 || _tree.Total[node] > _tree.Total[best])) best = node;
		}

		if (best < 0) return;

		_focus = best;
		Relayout();
	}

	private void Relayout() {
		_laidOutWidth = -1f;
		Repaint();
	}

	private void Layout(float width) {
		_laidOutWidth = width;
		_boxes.Clear();
		_maxDepth = 0;
		_hover = -1;

		long total = _focus == 0 ? _tree.RootTotal : _tree.Total[_focus];

		if (total <= 0 || width <= 0f) return;

		if (_focus == 0) {
			float cursor = 0f;

			for (int child = _tree.FirstChild[0]; child >= 0; child = _tree.NextSibling[child]) {
				float share = _tree.Total[child] / (float)total;
				Place(child, 0, cursor, cursor + share, width);
				cursor += share;
			}
		}
		else Place(_focus, 0, 0f, 1f, width);
	}

	private void Place(int node, int depth, float x0, float x1, float width) {
		if (x1 - x0 <= 0f) return;

		_boxes.Add(new Box(node, depth, x0, x1));
		_maxDepth = Math.Max(_maxDepth, depth);

		float span = x1 - x0;
		float cursor = x0;
		long total = _tree.Total[node];

		for (int child = _tree.FirstChild[node]; child >= 0; child = _tree.NextSibling[child]) {
			float share = _tree.Total[child] / (float)total * span;

			if (!ShowSystem && _tree.FirstChild[child] < 0 && FrameNames.IsSystem(_tree.Name(child), _tree.Module(child))) {
				cursor += share;
				continue;
			}

			if (share * width >= MinimumPixels) Place(child, depth + 1, cursor, cursor + share, width);

			cursor += share;
		}
	}

	private Color ColorFor(int node) {
		int frame = _tree.Frame[node];

		if (_colors.TryGetValue(frame, out Color color)) return color;

		string name = _tree.Names[frame];
		int hash = 0;

		foreach (char c in name) hash = unchecked((hash * 31) + c);

		hash = Math.Abs(hash);
		color = FrameNames.IsSystem(name, _tree.Modules[frame])
			? Hsl(210f, 0.12f, (62 + (hash % 10)) / 100f)
			: Hsl(20 + (hash % 40), (70 + ((hash >> 8) % 20)) / 100f, 0.62f);
		_colors[frame] = color;
		return color;
	}

	private static Color Hsl(float hue, float saturation, float lightness) {
		float chroma = (1f - MathF.Abs((2f * lightness) - 1f)) * saturation;
		float sector = hue / 60f;
		float second = chroma * (1f - MathF.Abs((sector % 2f) - 1f));
		(float r, float g, float b) = sector switch {
			< 1f => (chroma, second, 0f),
			< 2f => (second, chroma, 0f),
			< 3f => (0f, chroma, second),
			< 4f => (0f, second, chroma),
			< 5f => (second, 0f, chroma),
			_ => (chroma, 0f, second),
		};
		float m = lightness - (chroma / 2f);
		return new Color(r + m, g + m, b + m, 1f);
	}

	private int HitTest(float x, float y) {
		float row = RowHeight * Scale;
		int depth = (int)((y + _scroll) / row);
		float position = x / Wide;

		for (int i = 0; i < _boxes.Count; i++) {
			Box box = _boxes[i];

			if (box.Depth == depth && position >= box.X0 && position < box.X1) return i;
		}

		return -1;
	}

	public override void OnScaleChanged() => _laidOutWidth = -1f;

	public override void OnCursorMoved(float x, float y) {
		_mouseX = x;
		_mouseY = y;
		int hover = HitTest(x, y);

		if (hover == _hover && hover < 0) return;

		_hover = hover;
		Cursor = hover >= 0 ? CursorCode.Hand : CursorCode.Arrow;
		Repaint();
	}

	public override void OnCursorExited() {
		_hover = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button == MouseButton.Right) {
			ResetZoom();
			return true;
		}

		if (button != MouseButton.Left || HitTest(x, y) is not (var index and >= 0)) return false;

		int node = _boxes[index].Node;

		if ((Ui?.GetModifiers() & KeyModifiers.Alt) != 0) {
			OpenFrame?.Invoke(_tree.Name(node));
			return true;
		}

		_focus = node;
		Relayout();
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float content = (_maxDepth + 2) * RowHeight * Scale;
		float next = Math.Clamp(_scroll - (delta / 120f * RowHeight * 3f * Scale), 0f, MathF.Max(0f, content - Tall));

		if (next == _scroll) return false;

		_scroll = next;
		Repaint();
		return true;
	}

	public override void Paint(IPainter painter) {
		if (_laidOutWidth != Wide) Layout(Wide);

		float scale = Scale;
		float row = RowHeight * scale;
		ITextFont font = painter.GetMono(11f);

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		if (_boxes.Count == 0) {
			ITextFont ui = painter.GetUi();
			string message = "Record CPU to see a flame graph.";
			painter.DrawText(ui, message, ScreenX + ((Wide - ui.Measure(message)) / 2f), ScreenY + (Tall / 2f), Theme.DescriptionForeground);
			painter.PopClip();
			return;
		}

		for (int i = 0; i < _boxes.Count; i++) {
			Box box = _boxes[i];
			float x = ScreenX + (box.X0 * Wide);
			float width = (box.X1 - box.X0) * Wide;
			float y = ScreenY + (box.Depth * row) - _scroll;

			if (width < 0.5f || y + row < ScreenY || y > ScreenY + Tall || !painter.NeedsPaint(x, y, width, row)) continue;

			string name = _tree.Name(box.Node);
			bool dimmed = !Matches(name);
			Color color = ColorFor(box.Node);

			painter.FillRectangle(x, y, MathF.Max(width - 1f, 0.5f), row - 1f, dimmed ? color.WithAlpha(0.25f) : color);

			if (i == _hover) painter.StrokeRoundedRectangle(x + 0.5f, y + 0.5f, MathF.Max(width - 2f, 1f), row - 2f, 0f, 1.5f, Theme.FocusBorder);

			if (width <= 30f * scale) continue;

			painter.PushClip(x + (3f * scale), y, width - (6f * scale), row);
			painter.DrawText(font, (_short[_tree.Frame[box.Node]] ??= FrameNames.Short(name)), x + (4f * scale), y + ((row - font.GetLineHeight()) / 2f) + font.GetBaseline(), LabelColor);
			painter.PopClip();
		}

		if (_hover >= 0) DrawTooltip(painter, _boxes[_hover].Node, scale);

		painter.PopClip();
	}

	private void DrawTooltip(IPainter painter, int node, float scale) {
		ITextFont ui = painter.GetUi();
		ITextFont small = painter.GetUiSmall();
		string name = _tree.Name(node);
		string module = _tree.Module(node) is { Length: > 0 } value ? value : "(unknown module)";
		double percent = _tree.RootTotal > 0 ? _tree.Total[node] * 100.0 / _tree.RootTotal : 0;
		string stats = $"total {_format(_tree.Total[node])} · self {_format(_tree.Self[node])} · {percent:0.00}%";
		const string Hint = "click to zoom · right-click to reset · alt+click to find in workspace";

		float padding = 8f * scale;
		float width = MathF.Min(Wide - (16f * scale), MathF.Max(MathF.Max(ui.Measure(name), small.Measure(Hint)), small.Measure(stats)) + (padding * 2f));
		float height = (ui.GetLineHeight() + (small.GetLineHeight() * 3f)) + (padding * 2f);
		float x = ScreenX + MathF.Min(_mouseX + (12f * scale), Wide - width - (8f * scale));
		float y = ScreenY + MathF.Min(_mouseY + (14f * scale), Tall - height - (8f * scale));

		painter.FillRoundedRectangle(x, y, width, height, Metrics.RadiusMedium * scale, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(x, y, width, height, Metrics.RadiusMedium * scale, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
		painter.PushClip(x, y, width, height);

		float line = y + padding;
		painter.DrawText(ui, name, x + padding, line + ui.GetBaseline(), Theme.EditorForeground);
		line += ui.GetLineHeight();
		painter.DrawText(small, module, x + padding, line + small.GetBaseline(), Theme.DescriptionForeground);
		line += small.GetLineHeight();
		painter.DrawText(small, stats, x + padding, line + small.GetBaseline(), Theme.EditorForeground);
		line += small.GetLineHeight();
		painter.DrawText(small, Hint, x + padding, line + small.GetBaseline(), Theme.DescriptionForeground);

		painter.PopClip();
	}
}
