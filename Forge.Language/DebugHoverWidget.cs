using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Language;

public sealed class DebugHoverWidget : Panel
{
	public const string Tip = "Hold Alt key to switch to editor language hover";

	private const float RowHeight = 18f;
	private const float TitlePadding = 15f;
	private const float TreeIndent = 8f;
	private const float TwistieWidth = 16f;
	private const float ValueMargin = 6f;
	private const float SimplePaddingX = 5f;
	private const float SimplePaddingY = 4f;
	private const float MinimumTreeWidth = 400f;
	private const float MaximumTreeWidth = 550f;
	private const float MinimumTreeHeight = 266f;
	private const float ScrollbarAllowance = 10f;
	private const float SimpleMaximumWidth = 700f;
	private const float SimpleMaximumHeight = 500f;

	private static readonly Color NameForeground = Color.Parse("#c586c0");
	private static readonly Color ValueForeground = Color.Parse("#cccccc99");
	private static readonly Color StringForeground = Color.Parse("#ce9178");
	private static readonly Color BooleanForeground = Color.Parse("#4e94ce");
	private static readonly Color NumberForeground = Color.Parse("#b5cea8");
	private static readonly Color ErrorForeground = Color.Parse("#f48771");
	private static readonly Color Separator = Color.Parse("#80808059");

	private sealed class Node(DebugVariable variable, int depth)
	{
		public DebugVariable Variable { get; } = variable;
		public int Depth { get; } = depth;
		public bool Expanded { get; set; }
		public bool Loading { get; set; }
		public List<Node>? Children { get; set; }
	}

	private readonly List<Node> _roots = [];
	private readonly List<Node> _rows = [];
	private readonly List<string> _wrapped = [];

	private DebugVariable? _expression;
	private float _anchorX;
	private float _lineTop;
	private float _lineHeight;
	private float _scroll;

	public DebugHoverWidget() {
		Name = "DebugHover";
		Visible = false;
	}

	public Func<int, Task<IReadOnlyList<DebugVariable>>>? LoadChildren { get; set; }

	public Action? Changed { get; set; }

	public bool IsOpen { get; private set; }

	public int Line { get; private set; }

	public int StartColumn { get; private set; }

	public int EndColumn { get; private set; }

	public bool IsComplex => _expression is { VariablesReference: > 0 };

	public static Color ColorOf(DebugVariable variable, bool available = true) {
		if (!available) return ErrorForeground;

		ReadOnlySpan<char> value = variable.Value;

		if (variable.Type is "string" || (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])) return StringForeground;

		if (variable.Type is "bool" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return BooleanForeground;

		if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return NumberForeground;

		return ValueForeground;
	}

	public void Show(DebugVariable expression, int line, int startColumn, int endColumn, float anchorX, float lineTop, float lineHeight) {
		_expression = expression;
		Line = line;
		StartColumn = startColumn;
		EndColumn = endColumn;
		_anchorX = anchorX;
		_lineTop = lineTop;
		_lineHeight = lineHeight;
		_scroll = 0f;
		_roots.Clear();
		_rows.Clear();
		_wrapped.Clear();
		IsOpen = true;
		Visible = true;

		if (expression.VariablesReference > 0) _ = LoadAsync(null, expression.VariablesReference);

		Repaint();
	}

	public void Hide() {
		if (!IsOpen) return;

		IsOpen = false;
		Visible = false;
		_expression = null;
		_roots.Clear();
		_rows.Clear();
		Repaint();
	}

	private async Task LoadAsync(Node? parent, int reference) {
		if (LoadChildren is not { } load) return;

		if (parent is not null) parent.Loading = true;

		IReadOnlyList<DebugVariable> children = await load(reference).ConfigureAwait(false);
		List<Node> nodes = [.. children.Select(child => new Node(child, (parent?.Depth ?? -1) + 1))];

		if (parent is null) {
			_roots.Clear();
			_roots.AddRange(nodes);
		}
		else {
			parent.Children = nodes;
			parent.Loading = false;
		}

		Rebuild();
		Changed?.Invoke();
	}

	private void Rebuild() {
		_rows.Clear();

		foreach (Node root in _roots) Append(root);

		void Append(Node node) {
			_rows.Add(node);

			if (!node.Expanded || node.Children is null) return;

			foreach (Node child in node.Children) Append(child);
		}
	}

	private float TreeHeight(float editorHeight) {
		float scale = Scale;
		float content = (_rows.Count * RowHeight * scale) + (ScrollbarAllowance * scale);

		return MathF.Min(MathF.Max(MinimumTreeHeight * scale, editorHeight * 0.55f), content);
	}

	public void Place(float editorTop, float editorBottom, float editorRight, float originX, float originY) {
		Visible = IsOpen;

		if (!IsOpen || _expression is not { } expression || Ui is null) return;

		float scale = Scale;
		IFontSet fonts = Ui.GetFonts();
		ITextFont code = fonts.GetCode();
		float width;
		float height;

		if (expression.VariablesReference > 0) {
			float widest = 0f;

			foreach (Node row in _rows) widest = MathF.Max(widest, RowContentWidth(row, code, scale));

			width = Math.Clamp(widest, MinimumTreeWidth * scale, MaximumTreeWidth * scale);
			height = (RowHeight * scale * 2f) + TreeHeight(editorBottom - editorTop) + 2f;
		}
		else {
			float limit = SimpleMaximumWidth * scale;
			float padding = SimplePaddingX * scale * 2f;

			if (_wrapped.Count == 0) Wrap(expression.Value, code, limit - padding);

			float widest = 0f;

			foreach (string line in _wrapped) widest = MathF.Max(widest, code.Measure(line));

			width = MathF.Min(limit, widest + padding) + 2f;
			height = MathF.Min(SimpleMaximumHeight * scale, (_wrapped.Count * code.GetLineHeight()) + (SimplePaddingY * scale * 2f)) + 2f;
		}

		float top = _lineTop - height;

		if (top < editorTop) top = _lineTop + _lineHeight;

		float left = Math.Clamp(_anchorX, 0f, MathF.Max(0f, editorRight - width));
		SetBounds(left - originX, top - originY, width, height);
	}

	private void Wrap(string value, ITextFont font, float width) {
		foreach (string raw in value.ReplaceLineEndings("\n").Split('\n')) {
			ReadOnlySpan<char> rest = raw;

			while (rest.Length > 0 && font.Measure(rest) > width) {
				int cut = rest.Length;

				while (cut > 1 && font.Measure(rest[..cut]) > width) cut--;

				_wrapped.Add(rest[..cut].ToString());
				rest = rest[cut..];
			}

			_wrapped.Add(rest.ToString());
		}
	}

	private static float RowContentWidth(Node row, ITextFont font, float scale) {
		float label = font.Measure(row.Variable.Name) + font.Measure(" =") + (ValueMargin * scale) + font.Measure(row.Variable.Value);

		return (TreeIndent * scale * (row.Depth + 1)) + (TwistieWidth * scale) + label + (4f * scale);
	}

	public override void Paint(IPainter painter) {
		if (_expression is not { } expression) return;

		float scale = Scale;
		float radius = Metrics.RadiusMedium * scale;
		ITextFont code = painter.GetCode();

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.EditorHoverBorder);
		painter.PushClip(ScreenX + 1f, ScreenY + 1f, Wide - 2f, Tall - 2f);

		if (expression.VariablesReference <= 0) {
			float y = ScreenY + 1f + (SimplePaddingY * scale);
			Color color = ColorOf(expression);

			foreach (string line in _wrapped) {
				painter.DrawText(code, line, ScreenX + 1f + (SimplePaddingX * scale), y + code.GetBaseline(), color);
				y += code.GetLineHeight();
			}

			painter.PopClip();
			return;
		}

		ITextFont small = painter.GetUiDescription();
		float row = RowHeight * scale;
		float titleTop = ScreenY + 1f;

		painter.PushClip(ScreenX, titleTop, Wide, row);
		painter.DrawText(small, expression.Value, ScreenX + (TitlePadding * scale), titleTop + ((row - small.GetLineHeight()) / 2f) + small.GetBaseline(), ColorOf(expression));
		painter.PopClip();
		painter.FillRectangle(ScreenX, titleTop + row - 1f, Wide, 1f, Separator);

		float treeTop = titleTop + row;
		float treeHeight = Tall - 2f - (row * 2f);

		painter.PushClip(ScreenX, treeTop, Wide, treeHeight);

		int first = Math.Max(0, (int)(_scroll / row));
		int last = Math.Min(_rows.Count - 1, (int)((_scroll + treeHeight) / row) + 1);

		for (int i = first; i <= last; i++) {
			Node node = _rows[i];
			float top = treeTop + (i * row) - _scroll;
			float baseline = top + ((row - code.GetLineHeight()) / 2f) + code.GetBaseline();
			float x = ScreenX + (TreeIndent * scale * (node.Depth + 1));

			if (node.Variable.VariablesReference > 0) painter.DrawChevron(x + (TwistieWidth * scale / 2f), top + (row / 2f), 7f * scale, node.Expanded, Theme.Foreground);

			x += TwistieWidth * scale;

			painter.DrawText(code, node.Variable.Name, x, baseline, NameForeground);
			x += code.Measure(node.Variable.Name);
			painter.DrawText(code, " =", x, baseline, Theme.Foreground);
			x += code.Measure(" =") + (ValueMargin * scale);
			painter.DrawText(code, node.Variable.Value, x, baseline, ColorOf(node.Variable));
		}

		painter.PopClip();

		float tipTop = ScreenY + Tall - 1f - row;
		painter.FillRectangle(ScreenX, tipTop, Wide, 1f, Separator);
		painter.DrawText(small, Tip, ScreenX + (TitlePadding * scale), tipTop + ((row - small.GetLineHeight()) / 2f) + small.GetBaseline(), Theme.Foreground.WithAlpha(Theme.Foreground.A * 0.5f));
		painter.PopClip();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || !IsComplex) return true;

		float row = RowHeight * Scale;
		float relative = y - 1f - row + _scroll;

		if (relative < 0f) return true;

		int index = (int)(relative / row);

		if ((uint)index >= (uint)_rows.Count) return true;

		Node node = _rows[index];

		if (node.Variable.VariablesReference <= 0) return true;

		node.Expanded = !node.Expanded;

		if (node.Expanded && node.Children is null && !node.Loading) _ = LoadAsync(node, node.Variable.VariablesReference);

		Rebuild();
		Changed?.Invoke();
		Repaint();
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		if (!IsComplex) return true;

		float row = RowHeight * Scale;
		float visible = Tall - 2f - (row * 2f);
		float maximum = MathF.Max(0f, (_rows.Count * row) - visible);

		_scroll = Math.Clamp(_scroll - (delta / 120f * row * 3f), 0f, maximum);
		Repaint();
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (key != Core.Native.VirtualKey.Escape) return false;

		Hide();
		return true;
	}
}
