using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class DetailView : Panel
{
	private const float PaddingX = 12f;
	private const float PaddingY = 10f;
	private const int EdgeLimit = 12;
	private const float DragThreshold = 3f;

	private static readonly Color EdgeBar = Color.Parse("#569cd6").WithAlpha(0.22f);
	private static readonly Color Leaf = Color.Parse("#e06c75");

	private readonly List<Block> _blocks = [];
	private readonly List<Run> _runs = [];
	private readonly List<Shape> _shapes = [];
	private readonly List<Hit> _hits = [];
	private readonly List<string> _wrapped = [];

	private float _laidOutWidth = -1f;
	private float _contentHeight;
	private float _scroll;
	private int _hover = -1;
	private int _hoverStack = -1;
	private (int Run, int Offset) _anchor = (-1, 0);
	private (int Run, int Offset) _focus = (-1, 0);
	private bool _pressed;
	private bool _selecting;
	private float _pressX;
	private float _pressY;

	public abstract record Block;

	public sealed record Heading(string Text, bool Mono = true) : Block;

	public sealed record Hint(string Text) : Block;

	public sealed record Kpis(IReadOnlyList<(string Label, string Value, string Sub)> Items) : Block;

	public sealed record Verdict(string Lead, string Text) : Block;

	public sealed record Buttons(IReadOnlyList<(string Label, Action Action)> Items) : Block;

	public sealed record EdgeItem(double Share, string Value, string Name, bool System, Action? Click);

	public sealed record Edges(string Title, string Note, IReadOnlyList<EdgeItem> Items) : Block;

	public sealed record Section(string Title) : Block;

	public sealed record Stack(string Head, string Accent, string Note, IReadOnlyList<string> Frames) : Block;

	private readonly record struct Run(float X, float Y, string Text, ITextFont Font, Color Color, bool Joined, int Copy = -1);

	private enum ShapeKind : byte
	{
		Fill,
		Line,
		Button,
		Copy,
	}

	private readonly record struct Shape(ShapeKind Kind, float X, float Y, float Width, float Height, float Radius, Color Color, int Hit = -1, int Stack = -1);

	private readonly record struct Hit(float X, float Y, float Width, float Height, Action Action, int Stack = -1);

	public DetailView() {
		Name = "Detail";
		KeyboardInputEnabled = true;
	}

	public bool FullSignatures {
		get;
		set {
			if (field == value) return;

			field = value;
			Invalidate();
		}
	}

	public void Show(IEnumerable<Block> blocks) {
		_blocks.Clear();
		_blocks.AddRange(blocks);
		_scroll = 0f;
		ClearSelection();
		Invalidate();
	}

	public void Message(string text) => Show([new Hint(text)]);

	private void Invalidate() {
		_laidOutWidth = -1f;
		Repaint();
	}

	private void ClearSelection() {
		_anchor = (-1, 0);
		_focus = (-1, 0);
	}

	public override void OnScaleChanged() => _laidOutWidth = -1f;

	public override void OnSizeChanged(float wide, float tall) => _laidOutWidth = -1f;

	#region Layout

	private void Layout() {
		if (_laidOutWidth == Wide || Ui is null) return;

		_laidOutWidth = Wide;
		_runs.Clear();
		_shapes.Clear();
		_hits.Clear();

		IFontSet fonts = Ui.GetFonts();
		float scale = Scale;
		float left = PaddingX * scale;
		float width = MathF.Max(40f * scale, Wide - (PaddingX * 2f * scale));
		float y = PaddingY * scale;
		int stackIndex = 0;

		foreach (Block block in _blocks) {
			switch (block) {
				case Heading heading: {
					ITextFont font = heading.Mono ? fonts.GetMono(12f, strong: true) : fonts.GetUi(12f, strong: true);
					y = Text(font, heading.Text, left, y, width, Theme.EditorForeground, anywhere: true, lineHeight: 1.3f);
					y += 4f * scale;
					break;
				}

				case Hint hint:
					y = Text(fonts.GetUi(11.5f), hint.Text, left, y, MathF.Min(width, 70f * fonts.GetUi(11.5f).GetCellWidth()), Theme.EditorForeground.WithAlpha(0.65f), anywhere: false, lineHeight: 1.5f);
					y += 10f * scale;
					break;

				case Kpis kpis:
					y = LayoutKpis(fonts, kpis, left, y + (8f * scale), width, scale) + (8f * scale);
					break;

				case Verdict verdict: {
					y += 6f * scale;
					ITextFont strong = fonts.GetUi(12f, strong: true);
					ITextFont regular = fonts.GetUi(12f);
					y = Text(strong, verdict.Lead + " " + verdict.Text, left, y, width, Theme.EditorForeground, anywhere: false, lineHeight: 1.45f, boldPrefix: verdict.Lead.Length, regular: regular);
					y += 10f * scale;
					break;
				}

				case Buttons buttons:
					y = LayoutButtons(fonts, buttons, left, y, scale) + (12f * scale);
					break;

				case Edges edges:
					y = LayoutEdges(fonts, edges, left, y, width, scale);
					break;

				case Section section: {
					ITextFont font = fonts.GetUi(11.5f, strong: true);
					y += 10f * scale;
					_runs.Add(new Run(left, y, section.Title, font, Theme.EditorForeground, false));
					y += font.GetLineHeight() + (4f * scale);
					break;
				}

				case Stack stack:
					y = LayoutStack(fonts, stack, stackIndex++, left, y, width, scale) + (12f * scale);
					break;
			}
		}

		_contentHeight = y + (PaddingY * scale);
		_scroll = Math.Clamp(_scroll, 0f, MathF.Max(0f, _contentHeight - Tall));
	}

	private float Text(ITextFont font, string text, float x, float y, float width, Color color, bool anywhere, float lineHeight, int boldPrefix = 0, ITextFont? regular = null) {
		float step = MathF.Round(font.GetSizeInPixels() * lineHeight);
		float offset = (step - font.GetLineHeight()) / 2f;

		_wrapped.Clear();

		if (anywhere) WrapAnywhere(font, text, width, _wrapped);
		else WrapWords(font, text, width, _wrapped);

		int consumed = 0;

		for (int i = 0; i < _wrapped.Count; i++) {
			string line = _wrapped[i];
			bool joined = i > 0;

			if (regular is not null && consumed < boldPrefix) {
				int bold = Math.Min(boldPrefix - consumed, line.Length);
				_runs.Add(new Run(x, y + offset, line[..bold], font, color, joined));

				if (bold < line.Length) _runs.Add(new Run(x + font.Measure(line.AsSpan(0, bold)), y + offset, line[bold..], regular, color, true));
			}
			else _runs.Add(new Run(x, y + offset, line, regular ?? font, color, joined));

			consumed += line.Length;
			y += step;
		}

		return y;
	}

	private static void WrapAnywhere(ITextFont font, string text, float width, List<string> into) {
		int start = 0;
		float line = 0f;

		for (int i = 0; i < text.Length; i++) {
			float advance = font.GetAdvance(text[i]);

			if (line + advance > width && i > start) {
				into.Add(text[start..i]);
				start = i;
				line = 0f;
			}

			line += advance;
		}

		into.Add(text[start..]);
	}

	private static void WrapWords(ITextFont font, string text, float width, List<string> into) {
		int start = 0;
		int lastSpace = -1;
		float line = 0f;

		for (int i = 0; i < text.Length; i++) {
			if (text[i] == ' ') lastSpace = i;

			line += font.GetAdvance(text[i]);

			if (line <= width || i == start) continue;

			int end = lastSpace > start ? lastSpace + 1 : i;
			into.Add(text[start..end]);
			start = end;
			lastSpace = -1;
			line = font.Measure(text.AsSpan(start, i + 1 - start));
		}

		into.Add(text[start..]);
	}

	private float LayoutKpis(IFontSet fonts, Kpis kpis, float left, float y, float width, float scale) {
		ITextFont label = fonts.GetUi(10.5f);
		ITextFont value = fonts.GetUi(14f, strong: true);
		ITextFont sub = fonts.GetUi(11f);
		float gap = 6f * scale;
		int columns = Math.Max(1, Math.Min(kpis.Items.Count, (int)((width + gap) / ((80f * scale) + gap))));
		float cell = (width - (gap * (columns - 1))) / columns;
		float height = (12f * scale) + label.GetLineHeight() + value.GetLineHeight() + sub.GetLineHeight();

		for (int i = 0; i < kpis.Items.Count; i++) {
			(string name, string amount, string note) = kpis.Items[i];
			float x = left + ((i % columns) * (cell + gap));
			float top = y + ((i / columns) * (height + gap));
			float inner = x + (8f * scale);

			_shapes.Add(new Shape(ShapeKind.Fill, x, top, cell, height, 4f * scale, Theme.EditorForeground.WithAlpha(0.08f)));
			_runs.Add(new Run(inner, top + (6f * scale), name.ToUpperInvariant(), label, Theme.EditorForeground.WithAlpha(0.65f), false));
			_runs.Add(new Run(inner, top + (6f * scale) + label.GetLineHeight(), amount, value, Theme.EditorForeground, false));
			_runs.Add(new Run(inner, top + (6f * scale) + label.GetLineHeight() + value.GetLineHeight(), note.Length > 0 ? note : " ", sub, Theme.EditorForeground.WithAlpha(0.65f), false));
		}

		int rows = (kpis.Items.Count + columns - 1) / columns;
		return y + (rows * height) + ((rows - 1) * gap);
	}

	private float LayoutButtons(IFontSet fonts, Buttons buttons, float left, float y, float scale) {
		ITextFont font = fonts.GetUi(11f);
		float height = font.GetLineHeight() + (6f * scale);
		float x = left;

		foreach ((string label, Action action) in buttons.Items) {
			float width = font.Measure(label) + (16f * scale);
			int hit = _hits.Count;
			_hits.Add(new Hit(x, y, width, height, action));
			_shapes.Add(new Shape(ShapeKind.Button, x, y, width, height, 3f * scale, Theme.ButtonSecondaryBackground, hit));
			_runs.Add(new Run(x + (8f * scale), y + (3f * scale), label, font, Theme.ButtonSecondaryForeground, false));
			x += width + (6f * scale);
		}

		return y + height;
	}

	private float LayoutEdges(IFontSet fonts, Edges edges, float left, float y, float width, float scale) {
		if (edges.Items.Count == 0) return y;

		ITextFont head = fonts.GetUi(11.5f, strong: true);
		ITextFont note = fonts.GetUi(10.5f);
		ITextFont ui = fonts.GetUi(12f);
		ITextFont mono = fonts.GetMono(12f);

		y += 10f * scale;
		_runs.Add(new Run(left, y, edges.Title, head, Theme.EditorForeground, false));
		_runs.Add(new Run(left + head.Measure(edges.Title) + (8f * scale), y + head.GetBaseline() - note.GetBaseline(), edges.Note, note, Theme.EditorForeground.WithAlpha(0.55f), true));
		y += head.GetLineHeight() + (4f * scale);

		float row = ui.GetLineHeight() + (4f * scale);

		for (int i = 0; i < edges.Items.Count && i < EdgeLimit; i++) {
			EdgeItem item = edges.Items[i];
			float alpha = item.System ? 0.6f : 1f;
			float textY = y + (2f * scale);
			int hit = -1;

			if (item.Click is { } click) {
				hit = _hits.Count;
				_hits.Add(new Hit(left, y, width, row, click));
			}

			_shapes.Add(new Shape(ShapeKind.Fill, left, y, width, row, 3f * scale, Theme.ListHoverBackground, hit));
			_shapes.Add(new Shape(ShapeKind.Fill, left, y + scale, (float)(width * Math.Clamp(item.Share, 0, 1)), row - (2f * scale), 3f * scale, EdgeBar.WithAlpha(0.22f * alpha)));

			string pct = $"{item.Share * 100:0.0}%";
			float pctRight = left + ((6f + 44f) * scale);
			float valueRight = pctRight + ((8f + 72f) * scale);
			float nameX = valueRight + (8f * scale);

			_runs.Add(new Run(pctRight - ui.Measure(pct), textY, pct, ui, Theme.EditorForeground.WithAlpha(alpha), false));
			_runs.Add(new Run(valueRight - ui.Measure(item.Value), textY, item.Value, ui, Theme.EditorForeground.WithAlpha(0.7f * alpha), true));
			_runs.Add(new Run(nameX, textY + ((ui.GetLineHeight() - mono.GetLineHeight()) / 2f), Ellipsize(mono, FrameNames.Display(item.Name, FullSignatures), left + width - nameX - (6f * scale)), mono, Theme.EditorForeground.WithAlpha(alpha), true));

			y += row;
		}

		if (edges.Items.Count > EdgeLimit) {
			ITextFont more = fonts.GetUi(11f);
			_runs.Add(new Run(left + (6f * scale), y + (2f * scale), $"+{edges.Items.Count - EdgeLimit} more", more, Theme.EditorForeground.WithAlpha(0.55f), false));
			y += more.GetLineHeight() + (4f * scale);
		}

		return y + (10f * scale);
	}

	private static string Ellipsize(ITextFont font, string text, float width) {
		if (font.Measure(text) <= width) return text;

		float ellipsis = font.Measure("…");

		for (int length = text.Length - 1; length > 0; length--) {
			if (font.Measure(text.AsSpan(0, length)) + ellipsis <= width) return string.Concat(text.AsSpan(0, length), "…");
		}

		return "…";
	}

	private float LayoutStack(IFontSet fonts, Stack stack, int index, float left, float y, float width, float scale) {
		ITextFont head = fonts.GetUi(11f);
		ITextFont strong = fonts.GetUi(11f, strong: true);
		ITextFont copy = fonts.GetUi(10f);
		ITextFont mono = fonts.GetMono(11f);
		ITextFont monoStrong = fonts.GetMono(11f, strong: true);
		float headHeight = head.GetLineHeight() + (6f * scale);
		float baselineY = y + (3f * scale);

		_runs.Add(new Run(left, baselineY, stack.Head, strong, Theme.EditorForeground, false));
		float x = left + strong.Measure(stack.Head) + (8f * scale);
		_runs.Add(new Run(x, baselineY, stack.Accent, head, Theme.EditorForeground.WithAlpha(0.75f), true));

		float copyWidth = copy.Measure("Copy") + (12f * scale);
		float copyX = left + width - copyWidth;
		float noteX = copyX - (6f * scale) - head.Measure(stack.Note);
		_runs.Add(new Run(MathF.Max(x + head.Measure(stack.Accent) + (8f * scale), noteX), baselineY, stack.Note, head, Theme.EditorForeground.WithAlpha(0.5f), true));

		IReadOnlyList<string> frames = stack.Frames;
		int hit = _hits.Count;
		_hits.Add(new Hit(copyX, baselineY, copyWidth, copy.GetLineHeight() + (2f * scale), () => Clipboard.SetText(string.Join('\n', frames)), index));
		_shapes.Add(new Shape(ShapeKind.Copy, copyX, baselineY, copyWidth, copy.GetLineHeight() + (2f * scale), 3f * scale, Theme.ButtonSecondaryBackground, hit, index));
		_runs.Add(new Run(copyX + (6f * scale), baselineY + scale, "Copy", copy, Theme.ButtonSecondaryForeground, false, index));

		y += headHeight;
		_shapes.Add(new Shape(ShapeKind.Line, left, y - 1f, width, MathF.Max(1f, scale), 0f, Theme.EditorForeground.WithAlpha(0.18f)));
		y += 4f * scale;

		float lineStep = MathF.Round(11f * scale * 1.45f);
		float depth = 10f * scale;
		float stackTop = y;

		for (int i = 0; i < frames.Count; i++) {
			string frame = FrameNames.Display(frames[i], FullSignatures);
			bool leaf = i == 0;
			bool system = FrameNames.IsSystem(frames[i], string.Empty);
			ITextFont font = leaf ? monoStrong : mono;
			float indent = leaf ? 4f * scale : 0f;
			float textX = left + indent + depth;

			_wrapped.Clear();
			WrapAnywhere(font, frame, MathF.Max(20f * scale, width - indent - depth), _wrapped);

			float rowHeight = (_wrapped.Count * lineStep) + (4f * scale);
			Color background = leaf ? Leaf.WithAlpha(0.07f) : i % 2 == 1 ? Theme.EditorForeground.WithAlpha(0.05f) : Color.Transparent;

			if (background.A > 0f) _shapes.Add(new Shape(ShapeKind.Fill, left, y, width, rowHeight, 0f, background));

			if (leaf) {
				_shapes.Add(new Shape(ShapeKind.Fill, left, y, 2f * scale, rowHeight, 0f, Leaf.WithAlpha(0.75f)));
				_runs.Add(new Run(left + indent, y + (2f * scale) + ((lineStep - font.GetLineHeight()) / 2f), "▸", font, Theme.EditorForeground.WithAlpha(0.6f), false));
			}

			Color color = Theme.EditorForeground.WithAlpha(system ? 0.5f : 1f);

			for (int line = 0; line < _wrapped.Count; line++) _runs.Add(new Run(textX, y + (2f * scale) + (line * lineStep) + ((lineStep - font.GetLineHeight()) / 2f), _wrapped[line], font, color, leaf || line > 0));

			y += rowHeight;
		}

		_shapes.Add(new Shape(ShapeKind.Fill, left, stackTop - headHeight - (4f * scale), width, y - stackTop + headHeight + (4f * scale), 0f, Color.Transparent, Stack: index));
		return y;
	}

	#endregion

	#region Selection

	private (int Run, int Offset) PositionAt(float x, float y) {
		float absoluteY = y + _scroll;
		int best = -1;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < _runs.Count; i++) {
			Run run = _runs[i];
			float height = run.Font.GetLineHeight();
			float width = run.Font.Measure(run.Text);
			float dy = absoluteY < run.Y ? run.Y - absoluteY : absoluteY > run.Y + height ? absoluteY - run.Y - height : 0f;
			float dx = x < run.X ? run.X - x : x > run.X + width ? x - run.X - width : 0f;
			float distance = (dy * 1000f) + dx;

			if (distance >= bestDistance) continue;

			bestDistance = distance;
			best = i;
		}

		if (best < 0) return (-1, 0);

		Run hit = _runs[best];
		float cursor = hit.X;

		for (int c = 0; c < hit.Text.Length; c++) {
			float advance = hit.Font.GetAdvance(hit.Text[c]);

			if (x < cursor + (advance / 2f)) return (best, c);

			cursor += advance;
		}

		return (best, hit.Text.Length);
	}

	private ((int Run, int Offset) Start, (int Run, int Offset) End) Ordered() =>
		_anchor.Run < _focus.Run || (_anchor.Run == _focus.Run && _anchor.Offset <= _focus.Offset) ? (_anchor, _focus) : (_focus, _anchor);

	private bool HasSelection => _anchor.Run >= 0 && _focus.Run >= 0 && _anchor != _focus;

	private string SelectedText() {
		if (!HasSelection) return string.Empty;

		((int startRun, int startOffset), (int endRun, int endOffset)) = Ordered();
		System.Text.StringBuilder builder = new();

		for (int i = startRun; i <= endRun; i++) {
			Run run = _runs[i];
			int from = i == startRun ? startOffset : 0;
			int to = i == endRun ? endOffset : run.Text.Length;

			if (run.Copy >= 0) continue;

			if (i > startRun) builder.Append(run.Joined ? (run.Y == _runs[i - 1].Y ? " " : string.Empty) : "\n");

			builder.Append(run.Text.AsSpan(from, Math.Max(0, to - from)));
		}

		return builder.ToString();
	}

	#endregion

	#region Input

	private int HitAt(float x, float y) {
		float absoluteY = y + _scroll;

		for (int i = 0; i < _hits.Count; i++) {
			Hit hit = _hits[i];

			if (x >= hit.X && x < hit.X + hit.Width && absoluteY >= hit.Y && absoluteY < hit.Y + hit.Height) return i;
		}

		return -1;
	}

	private int StackAt(float y) {
		float absoluteY = y + _scroll;

		foreach (Shape shape in _shapes) {
			if (shape.Stack >= 0 && shape.Kind == ShapeKind.Fill && absoluteY >= shape.Y && absoluteY < shape.Y + shape.Height) return shape.Stack;
		}

		return -1;
	}

	private bool OverText(float x, float y) {
		float absoluteY = y + _scroll;

		foreach (Run run in _runs) {
			if (absoluteY >= run.Y && absoluteY < run.Y + run.Font.GetLineHeight() && x >= run.X && x < run.X + run.Font.Measure(run.Text)) return true;
		}

		return false;
	}

	public override void OnCursorMoved(float x, float y) {
		Layout();

		if (_pressed) {
			if (!_selecting && (MathF.Abs(x - _pressX) > DragThreshold * Scale || MathF.Abs(y - _pressY) > DragThreshold * Scale)) _selecting = true;

			if (_selecting) {
				if (y < 0f) _scroll = MathF.Max(0f, _scroll - (8f * Scale));
				else if (y > Tall) _scroll = MathF.Min(MathF.Max(0f, _contentHeight - Tall), _scroll + (8f * Scale));

				_focus = PositionAt(x, y);
				Repaint();
			}

			return;
		}

		int hover = HitAt(x, y);
		int stack = StackAt(y);
		Cursor = hover >= 0 ? CursorCode.Hand : OverText(x, y) ? CursorCode.IBeam : CursorCode.Arrow;

		if (hover == _hover && stack == _hoverStack) return;

		_hover = hover;
		_hoverStack = stack;
		Repaint();
	}

	public override void OnCursorExited() {
		if (_pressed) return;

		_hover = -1;
		_hoverStack = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		Layout();
		RequestFocus();
		_pressed = true;
		_selecting = false;
		_pressX = x;
		_pressY = y;

		if ((Ui?.GetModifiers() & KeyModifiers.Shift) != 0 && _anchor.Run >= 0) {
			_selecting = true;
			_focus = PositionAt(x, y);
		}
		else {
			_anchor = PositionAt(x, y);
			_focus = _anchor;
		}

		Ui?.SetMouseCapture(this);
		Repaint();
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		Layout();
		(int run, int offset) = PositionAt(x, y);

		if (run < 0) return true;

		string text = _runs[run].Text;
		int start = offset;
		int end = offset;

		while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('.' or '(' or ')' or ',')) start--;

		while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('.' or '(' or ')' or ',')) end++;

		_anchor = (run, start);
		_focus = (run, end);
		Repaint();
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_pressed) return false;

		_pressed = false;
		Ui?.SetMouseCapture(null);

		if (!_selecting && HitAt(x, y) is var hit and >= 0) {
			ClearSelection();
			Repaint();
			_hits[hit].Action();
			return true;
		}

		_selecting = false;
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if ((modifiers & KeyModifiers.Control) == 0) return false;

		switch (key) {
			case VirtualKey.C when HasSelection:
				Clipboard.SetText(SelectedText());
				return true;

			case VirtualKey.A when _runs.Count > 0:
				_anchor = (0, 0);
				_focus = (_runs.Count - 1, _runs[^1].Text.Length);
				Repaint();
				return true;
		}

		return false;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		Layout();

		float next = Math.Clamp(_scroll - (delta / 120f * 48f * Scale), 0f, MathF.Max(0f, _contentHeight - Tall));

		if (next == _scroll) return false;

		_scroll = next;
		Repaint();
		return true;
	}

	#endregion

	public override void Paint(IPainter painter) {
		Layout();

		float originX = ScreenX;
		float originY = ScreenY - _scroll;

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		foreach (Shape shape in _shapes) {
			float y = originY + shape.Y;

			if (y > ScreenY + Tall || y + shape.Height < ScreenY) continue;

			switch (shape.Kind) {
				case ShapeKind.Fill when shape.Hit >= 0:
					if (shape.Hit == _hover) painter.FillRoundedRectangle(originX + shape.X, y, shape.Width, shape.Height, shape.Radius, shape.Color);

					break;

				case ShapeKind.Fill when shape.Color.A > 0f:
					painter.FillRoundedRectangle(originX + shape.X, y, shape.Width, shape.Height, shape.Radius, shape.Color);
					break;

				case ShapeKind.Line:
					painter.FillRectangle(originX + shape.X, y, shape.Width, shape.Height, shape.Color);
					break;

				case ShapeKind.Button:
					painter.FillRoundedRectangle(originX + shape.X, y, shape.Width, shape.Height, shape.Radius, shape.Hit == _hover ? Theme.ButtonSecondaryHoverBackground : shape.Color);
					break;

				case ShapeKind.Copy when shape.Stack == _hoverStack:
					painter.FillRoundedRectangle(originX + shape.X, y, shape.Width, shape.Height, shape.Radius, shape.Hit == _hover ? Theme.ButtonSecondaryHoverBackground : shape.Color.WithAlpha(0.85f));
					break;
			}
		}

		bool selection = HasSelection;
		((int Run, int Offset) start, (int Run, int Offset) end) = selection ? Ordered() : ((-1, 0), (-1, 0));

		for (int i = 0; i < _runs.Count; i++) {
			Run run = _runs[i];
			float y = originY + run.Y;
			float height = run.Font.GetLineHeight();

			if (y > ScreenY + Tall || y + height < ScreenY) continue;

			if (run.Copy >= 0 && run.Copy != _hoverStack) continue;

			if (selection && run.Copy < 0 && i >= start.Run && i <= end.Run) {
				int from = i == start.Run ? start.Offset : 0;
				int to = i == end.Run ? end.Offset : run.Text.Length;

				if (to > from) {
					float selectionX = originX + run.X + run.Font.Measure(run.Text.AsSpan(0, from));
					painter.FillRectangle(selectionX, y, run.Font.Measure(run.Text.AsSpan(from, to - from)), height, HasFocus ? Theme.SelectionBackground : Theme.SelectionBackground.WithAlpha(0.6f));
				}
			}

			painter.DrawText(run.Font, run.Text, originX + run.X, y + run.Font.GetBaseline(), run.Color);
		}

		painter.PopClip();
	}
}
