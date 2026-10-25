using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core;
using Forge.Core.Native;
using Forge.Editor;
using Forge.Text;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class PeekReferencesWidget : Panel
{
	public const int DefaultHeightInLines = 18;
	public const float DefaultRatio = 0.7f;

	private const float RowHeight = 23f;
	private const float TitleMargin = 20f;
	private const float TreeIndent = 8f;
	private const float TwistieWidth = 16f;
	private const float CountMargin = 12f;
	private const float SashSize = 4f;
	private const float ActionSize = 22f;
	private const float PreviewMinimum = 200f;
	private const float TreeMinimum = 100f;
	private const int PreviewContext = 8;

	public sealed class Reference(HostLocation location, int group)
	{
		public HostLocation Location { get; } = location;
		public int Group { get; } = group;
		public string Preview { get; set; } = string.Empty;
		public int HighlightStart { get; set; }
		public int HighlightLength { get; set; }
	}

	public sealed class Group(string path)
	{
		public string Path { get; } = path;
		public List<Reference> References { get; } = [];
		public bool Expanded { get; set; }
		public TextBuffer? Buffer { get; set; }
	}

	private readonly record struct Row(int Group, int Reference);

	private readonly ActionButton _close = new() { Icon = Codicon.Close };
	private readonly List<Group> _groups = [];
	private readonly List<Row> _rows = [];
	private TextView _preview = new(new TextBuffer(string.Empty)) { Background = Theme.PeekViewEditorBackground, CaretVisible = false };
	private readonly List<TextView.Decoration> _decorations = [];

	private Reference? _selected;
	private Group? _previewGroup;
	private float _treeScroll;
	private int _hoverRow = -1;
	private bool _draggingSplit;
	private bool _draggingHeight;
	private float _dragStartY;
	private int _dragStartLines;

	public PeekReferencesWidget() {
		Name = "PeekReferences";
		Visible = false;
		_close.Action = Close;
		AddChild(_close);
	}

	public string Title { get; private set; } = "References";

	public bool IsOpen { get; private set; }

	public int ZoneId { get; set; } = -1;

	public int Line { get; private set; }

	public int Column { get; private set; }

	public float Ratio { get; set; } = DefaultRatio;

	public int HeightInLines { get; set; } = DefaultHeightInLines;

	public float ArrowX { get; set; }

	public float LineHeight { get; set; }

	public Action? Closed { get; set; }

	public Action<HostLocation, bool>? Open { get; set; }

	public Action? LayoutChanged { get; set; }

	public IReadOnlyList<Group> Groups => _groups;

	public Reference? Selected => _selected;

	public void Show(string title, int line, int column, IReadOnlyList<HostLocation> locations, Func<string, TextBuffer?> load) {
		Title = title;
		Line = line;
		Column = column;
		IsOpen = true;
		Visible = true;
		_groups.Clear();
		_selected = null;
		_previewGroup = null;
		_treeScroll = 0f;

		foreach (HostLocation location in locations) {
			int index = _groups.FindIndex(group => string.Equals(group.Path, location.Path, StringComparison.OrdinalIgnoreCase));

			if (index < 0) {
				index = _groups.Count;
				_groups.Add(new Group(location.Path));
			}

			_groups[index].References.Add(new Reference(location, index));
		}

		_groups.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));

		for (int g = 0; g < _groups.Count; g++) {
			Group group = _groups[g];
			group.References.Sort(static (left, right) => left.Location.Line != right.Location.Line ? left.Location.Line.CompareTo(right.Location.Line) : left.Location.Column.CompareTo(right.Location.Column));
			group.Buffer = load(group.Path);

			for (int r = 0; r < group.References.Count; r++) {
				Reference reference = new(group.References[r].Location, g);
				Describe(reference, group.Buffer);
				group.References[r] = reference;
			}
		}

		Rebuild();
		Repaint();
	}

	public static (string Value, int Start, int Length) PreviewOf(ReadOnlySpan<char> lineText, int startColumn, int endColumn) {
		int start = Math.Clamp(startColumn - 1, 0, lineText.Length);
		int end = Math.Clamp(endColumn - 1, start, lineText.Length);
		int wordStart = Math.Max(0, start - PreviewContext);

		while (wordStart > 0 && IsWordCharacter(lineText[wordStart - 1])) wordStart--;

		ReadOnlySpan<char> before = lineText[wordStart..start].TrimStart();
		ReadOnlySpan<char> inside = lineText[start..end];
		ReadOnlySpan<char> after = lineText[end..].TrimEnd();

		return (string.Concat(before, inside, after), before.Length, inside.Length);
	}

	private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

	private static void Describe(Reference reference, TextBuffer? buffer) {
		HostLocation location = reference.Location;

		if (buffer is null || location.Line < 1 || location.Line > buffer.LineCount) {
			reference.Preview = $"{System.IO.Path.GetFileName(location.Path)}:{location.Line}:{location.Column}";
			return;
		}

		int lineStart = buffer.GetLineStartOffset(location.Line - 1);
		int lineLength = buffer.GetLineLength(location.Line - 1);
		Span<char> text = lineLength <= 1024 ? stackalloc char[lineLength] : new char[lineLength];
		buffer.CopyTo(lineStart, lineLength, text);

		int endColumn = location.EndLine == location.Line ? location.EndColumn : lineLength + 1;
		(string value, int start, int length) = PreviewOf(text, location.Column, endColumn);

		reference.Preview = value;
		reference.HighlightStart = start;
		reference.HighlightLength = length;
	}

	public void SelectNearest(string path, int line, int column) {
		Reference? best = null;

		foreach (Group group in _groups) {
			if (!string.Equals(group.Path, path, StringComparison.OrdinalIgnoreCase)) continue;

			foreach (Reference reference in group.References) {
				best ??= reference;

				if (reference.Location.Line < line || (reference.Location.Line == line && reference.Location.Column <= column)) best = reference;
			}
		}

		best ??= _groups.Count > 0 && _groups[0].References.Count > 0 ? _groups[0].References[0] : null;

		if (best is not null) Select(best, revealParent: true);
	}

	public void Select(Reference reference, bool revealParent) {
		_selected = reference;
		Group group = _groups[reference.Group];

		if (!group.Expanded) {
			group.Expanded = true;
			Rebuild();
		}

		if (revealParent) RevealSelected();

		ShowInPreview(group, reference);
		Repaint();
	}

	private void ShowInPreview(Group group, Reference reference) {
		if (group.Buffer is not { } buffer) return;

		if (_previewGroup != group) {
			_previewGroup = group;
			_preview = new TextView(buffer, group.Path) { Background = Theme.PeekViewEditorBackground, CaretVisible = false };
			_decorations.Clear();

			foreach (Reference other in group.References) {
				HostLocation location = other.Location;
				int endColumn = location.EndLine == location.Line ? location.EndColumn : int.MaxValue;
				_decorations.Add(new TextView.Decoration(location.Line - 1, location.Column - 1, endColumn == int.MaxValue ? int.MaxValue : endColumn - 1, Theme.PeekViewEditorMatchHighlight.ToArgb()));
			}

			_preview.Decorations = _decorations;
		}

		int line = Math.Clamp(reference.Location.Line - 1, 0, Math.Max(0, buffer.LineCount - 1));
		int offset = Math.Min(buffer.GetLineStartOffset(line) + reference.Location.Column - 1, buffer.GetLineStartOffset(line) + buffer.GetLineLength(line));

		_preview.MoveTo(offset, extend: false);
		_preview.CenterLineOnNextRender = line;
	}

	private void Rebuild() {
		_rows.Clear();
		bool flat = _groups.Count == 1;

		for (int g = 0; g < _groups.Count; g++) {
			if (!flat) _rows.Add(new Row(g, -1));

			if (!flat && !_groups[g].Expanded) continue;

			for (int r = 0; r < _groups[g].References.Count; r++) _rows.Add(new Row(g, r));
		}
	}

	public void Close() {
		if (!IsOpen) return;

		IsOpen = false;
		Visible = false;
		_groups.Clear();
		_rows.Clear();
		_selected = null;
		_previewGroup = null;
		Repaint();
		Closed?.Invoke();
	}

	public float ZoneHeight(float lineHeight, float editorHeight) {
		float maximum = MathF.Max(12f, editorHeight / lineHeight * 0.8f);

		return MathF.Min(HeightInLines, maximum) * lineHeight;
	}

	private float ArrowHeight => MathF.Round(LineHeight / 3f);

	private float HeadHeight => MathF.Ceiling(LineHeight * 1.2f);

	private float ContainerTop => ArrowHeight;

	private float ContainerHeight => Tall - (ArrowHeight * 2f);

	private float BodyTop => ContainerTop + 1f + HeadHeight + 1f;

	private float BodyHeight => MathF.Max(0f, ContainerTop + ContainerHeight - 1f - BodyTop);

	private float PreviewWidth => Math.Clamp(Wide * Ratio, PreviewMinimum * Scale, MathF.Max(PreviewMinimum * Scale, Wide - (TreeMinimum * Scale)));

	private float RowPixels => RowHeight * Scale;

	public override void PerformLayout() {
		float size = ActionSize * Scale;
		_close.SetBounds(Wide - size - (2f * Scale), ContainerTop + 1f + ((HeadHeight - size) / 2f), size, size);
	}

	private void RevealSelected() {
		if (_selected is null) return;

		int index = _rows.FindIndex(row => row.Reference >= 0 && _groups[row.Group].References[row.Reference] == _selected);

		if (index < 0) return;

		float top = index * RowPixels;

		if (top < _treeScroll) _treeScroll = top;
		else if (top + RowPixels > _treeScroll + BodyHeight) _treeScroll = top + RowPixels - BodyHeight;
	}

	public override void Paint(IPainter painter) {
		if (!IsOpen) return;

		float scale = Scale;
		float x = ScreenX;
		float y = ScreenY;
		float arrow = ArrowHeight;
		float containerTop = y + ContainerTop;
		float containerBottom = containerTop + ContainerHeight;

		float arrowX = x + ArrowX;
		painter.FillTriangle(arrowX - arrow, containerTop, arrowX + arrow, containerTop, arrowX, containerTop - arrow, Theme.PeekViewBorder);

		painter.FillRectangle(x, containerTop, Wide, 1f, Theme.PeekViewBorder);
		painter.FillRectangle(x, containerBottom - 1f, Wide, 1f, Theme.PeekViewBorder);

		float headTop = containerTop + 1f;
		float head = HeadHeight;
		painter.FillRectangle(x, headTop, Wide, head, Theme.PeekViewTitleBackground);
		PaintTitle(painter, x + (TitleMargin * scale), headTop, head);

		float bodyTop = y + BodyTop;
		painter.FillRectangle(x, bodyTop - 1f, Wide, 1f, Theme.PeekViewBorder);

		float previewWidth = PreviewWidth;
		float bodyHeight = BodyHeight;

		painter.PushClip(x, bodyTop, previewWidth, bodyHeight);

		if (_preview.CenterLineOnNextRender is int center) {
			_preview.CenterLineOnNextRender = null;
			_preview.RevealLineInCenter(center, painter.GetCode().GetLineHeight(), bodyHeight);
		}

		_preview.Render(painter, x, bodyTop, previewWidth, bodyHeight);
		painter.PopClip();

		PaintTree(painter, x + previewWidth, bodyTop, Wide - previewWidth, bodyHeight);
	}

	private void PaintTitle(IPainter painter, float left, float top, float height) {
		ITextFont font = painter.GetUi();
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float limit = ScreenX + Wide - (ActionSize * Scale) - (4f * Scale);
		float cursor = left;

		painter.PushClip(left, top, MathF.Max(0f, limit - left), height);

		if (_selected is { } selected) {
			string path = selected.Location.Path;
			ReadOnlySpan<char> name = System.IO.Path.GetFileName(path.AsSpan());
			ReadOnlySpan<char> directory = System.IO.Path.GetDirectoryName(path.AsSpan());

			painter.DrawText(font, name, cursor, baseline, Theme.PeekViewTitleForeground);
			cursor += font.Measure(name);

			if (!directory.IsEmpty) {
				ITextFont small = painter.GetUiDescription();
				cursor += font.GetSizeInPixels() * 0.5f;
				painter.DrawText(small, directory, cursor, baseline, Theme.PeekViewTitleInfoForeground);
				cursor += small.Measure(directory);
			}
		}

		if (_groups.Count > 0) {
			int count = 0;

			foreach (Group group in _groups) count += group.References.Count;

			string meta = $"{Title} ({count})";
			float pad = font.GetSizeInPixels() * 0.3f;

			cursor += pad;
			painter.DrawText(font, "-", cursor, baseline, Theme.Foreground);
			cursor += font.Measure("-") + pad;
			painter.DrawText(font, meta, cursor, baseline, Theme.Foreground);
		}

		painter.PopClip();
	}

	private void PaintTree(IPainter painter, float left, float top, float width, float height) {
		float scale = Scale;
		float row = RowPixels;
		ITextFont font = painter.GetUi();
		ITextFont small = painter.GetUiDescription();
		float icon = Metrics.IconSize * scale;

		painter.FillRectangle(left, top, width, height, Theme.PeekViewResultBackground);
		painter.PushClip(left, top, width, height);

		int first = Math.Max(0, (int)(_treeScroll / row));
		int last = Math.Min(_rows.Count - 1, (int)((_treeScroll + height) / row) + 1);
		bool flat = _groups.Count == 1;

		for (int i = first; i <= last; i++) {
			Row entry = _rows[i];
			float rowTop = top + (i * row) - _treeScroll;
			float baseline = rowTop + ((row - font.GetLineHeight()) / 2f) + font.GetBaseline();
			Group group = _groups[entry.Group];
			bool selected = entry.Reference >= 0 && group.References[entry.Reference] == _selected;

			if (selected) painter.FillRectangle(left, rowTop, width, row, Theme.PeekViewResultSelectionBackground);
			else if (i == _hoverRow) painter.FillRectangle(left, rowTop, width, row, Theme.ListHoverBackground);

			if (entry.Reference < 0) {
				float cursor = left + (TreeIndent * scale);

				painter.DrawChevron(cursor + (TwistieWidth * scale / 2f), rowTop + (row / 2f), 8f * scale, group.Expanded, Theme.PeekViewResultFileForeground);
				cursor += TwistieWidth * scale;

				ReadOnlySpan<char> name = System.IO.Path.GetFileName(group.Path.AsSpan());

				if (painter.DrawFileIcon(name, cursor + (icon / 2f), rowTop + (row / 2f), icon)) cursor += icon + (Metrics.Space6 * scale);

				painter.DrawText(font, name, cursor, baseline, Theme.PeekViewResultFileForeground);
				cursor += font.Measure(name) + (font.GetSizeInPixels() * 0.5f);

				ReadOnlySpan<char> directory = System.IO.Path.GetDirectoryName(group.Path.AsSpan());
				string count = group.References.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
				float badgeWidth = MathF.Max(18f * scale, small.Measure(count) + (12f * scale));
				float badgeHeight = 18f * scale;
				float badgeLeft = left + width - (CountMargin * scale) - badgeWidth;

				painter.PushClip(cursor, rowTop, MathF.Max(0f, badgeLeft - cursor - (4f * scale)), row);
				painter.DrawText(small, directory, cursor, baseline, Theme.DescriptionForeground);
				painter.PopClip();

				painter.FillRoundedRectangle(badgeLeft, rowTop + ((row - badgeHeight) / 2f), badgeWidth, badgeHeight, badgeHeight / 2f, Theme.BadgeBackground);
				painter.DrawText(small, count, badgeLeft + ((badgeWidth - small.Measure(count)) / 2f), rowTop + ((row - small.GetLineHeight()) / 2f) + small.GetBaseline(), Theme.BadgeForeground);
				continue;
			}

			Reference reference = group.References[entry.Reference];
			float textLeft = left + (TreeIndent * scale * (flat ? 1f : 2f)) + (TwistieWidth * scale);
			ReadOnlySpan<char> preview = reference.Preview;
			int start = Math.Clamp(reference.HighlightStart, 0, preview.Length);
			int length = Math.Clamp(reference.HighlightLength, 0, preview.Length - start);
			Color foreground = selected ? Theme.PeekViewResultSelectionForeground : Theme.PeekViewResultLineForeground;

			float before = font.Measure(preview[..start]);
			float inside = font.Measure(preview.Slice(start, length));

			if (length > 0) painter.FillRectangle(textLeft + before, rowTop + ((row - font.GetLineHeight()) / 2f), inside, font.GetLineHeight(), Theme.PeekViewResultMatchHighlight);

			painter.DrawText(font, preview[..start], textLeft, baseline, foreground);
			painter.DrawText(font, preview.Slice(start, length), textLeft + before, baseline, Theme.PeekViewResultFileForeground);
			painter.DrawText(font, preview[(start + length)..], textLeft + before + inside, baseline, foreground);
		}

		painter.PopClip();
	}

	private int RowAt(float y) {
		float relative = y - BodyTop + _treeScroll;

		if (relative < 0f) return -1;

		int index = (int)(relative / RowPixels);

		return index < _rows.Count ? index : -1;
	}

	private bool InTree(float x, float y) => x >= PreviewWidth && y >= BodyTop && y < BodyTop + BodyHeight;

	private bool InPreview(float x, float y) => x < PreviewWidth && y >= BodyTop && y < BodyTop + BodyHeight;

	private bool OnSplitSash(float x, float y) => y >= BodyTop && MathF.Abs(x - PreviewWidth) <= SashSize * Scale / 2f;

	private bool OnHeightSash(float y) => MathF.Abs(y - (ContainerTop + ContainerHeight)) <= SashSize * Scale / 2f;

	private bool InTitle(float x, float y) => y >= ContainerTop && y < ContainerTop + 1f + HeadHeight && x < Wide - (ActionSize * Scale) - (4f * Scale);

	public override void OnCursorMoved(float x, float y) {
		if (_draggingSplit) {
			Ratio = Math.Clamp(x / MathF.Max(1f, Wide), 0.1f, 0.9f);
			Repaint();
			return;
		}

		if (_draggingHeight) {
			int lines = _dragStartLines + (int)MathF.Round((y - _dragStartY) / MathF.Max(1f, LineHeight));
			lines = Math.Max(4, lines);

			if (lines != HeightInLines) {
				HeightInLines = lines;
				LayoutChanged?.Invoke();
			}

			return;
		}

		Cursor = OnSplitSash(x, y) ? CursorCode.SizeWestEast : OnHeightSash(y) ? CursorCode.SizeNorthSouth : InTitle(x, y) ? CursorCode.Hand : CursorCode.Arrow;

		int hover = InTree(x, y) ? RowAt(y) : -1;

		if (hover == _hoverRow) return;

		_hoverRow = hover;
		Repaint();
	}

	public override void OnCursorExited() {
		if (_hoverRow < 0) return;

		_hoverRow = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) => Press(button, x, y, 1);

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) => Press(button, x, y, 2);

	private bool Press(MouseButton button, float x, float y, int clicks) {
		RequestFocus();

		if (button != MouseButton.Left) return true;

		if (OnSplitSash(x, y)) {
			_draggingSplit = true;
			Ui?.SetMouseCapture(this);
			return true;
		}

		if (OnHeightSash(y)) {
			_draggingHeight = true;
			_dragStartY = y;
			_dragStartLines = HeightInLines;
			Ui?.SetMouseCapture(this);
			return true;
		}

		if (InTitle(x, y)) {
			if (_selected is { } current) Open?.Invoke(current.Location, false);

			return true;
		}

		if (InTree(x, y) && RowAt(y) is >= 0 and int index) {
			Row row = _rows[index];
			Group group = _groups[row.Group];

			if (row.Reference < 0) {
				group.Expanded = !group.Expanded;
				Rebuild();
				Repaint();
				return true;
			}

			Reference reference = group.References[row.Reference];
			Select(reference, revealParent: false);

			if (clicks == 2) Open?.Invoke(reference.Location, true);

			return true;
		}

		if (InPreview(x, y) && clicks == 2 && _selected is { } focused) Open?.Invoke(focused.Location with { Line = PreviewLineAt(y) + 1, Column = 1 }, false);

		return true;
	}

	private int PreviewLineAt(float y) => _preview.LineAtY(y - BodyTop + _preview.ScrollY, LineHeight);

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_draggingSplit && !_draggingHeight) return false;

		_draggingSplit = false;
		_draggingHeight = false;
		Ui?.SetMouseCapture(null);
		LayoutChanged?.Invoke();
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		if (InTree(x, y)) {
			float maximum = MathF.Max(0f, (_rows.Count * RowPixels) - BodyHeight);
			_treeScroll = Math.Clamp(_treeScroll - (delta / 120f * RowPixels * 3f), 0f, maximum);
			Repaint();
			return true;
		}

		if (InPreview(x, y)) {
			_preview.ScrollByWheel(delta, LineHeight, BodyHeight);
			Repaint();
			return true;
		}

		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (key == VirtualKey.Escape) {
			Close();
			return true;
		}

		if (key == VirtualKey.Return && _selected is { } selected) {
			Open?.Invoke(selected.Location, false);
			return true;
		}

		if (key is VirtualKey.Down or VirtualKey.Up && _selected is { } current) {
			List<Reference> all = [.. _groups.SelectMany(static group => group.References)];
			int index = all.IndexOf(current) + (key == VirtualKey.Down ? 1 : -1);

			if ((uint)index < (uint)all.Count) Select(all[index], revealParent: true);

			return true;
		}

		return false;
	}
}
