using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Text;
using Forge.Theming;

namespace Forge.Editor;

public sealed class DiffEditor
{
	public const float GutterWidth = 35f;
	public const float OverviewWidth = 30f;
	public const float InlineBreakpoint = 900f;

	private const float HiddenBarHeight = 24f;
	private const int ContextLines = 3;
	private const int MinimumHidden = 3;
	private const int RevealLines = 20;
	private const float ButtonWidth = 20f;
	private const float ButtonHeight = 18f;

	private static readonly Color DiagonalFill = Color.Parse("#cccccc33");
	private static readonly Color UnchangedRegionBackground = Color.Parse("#0f131b");
	private static readonly Color UnchangedRegionForeground = Color.Parse("#c9d1dc");
	private static readonly Color GutterLine = Color.Parse("#1d2638");
	private static readonly Color GutterItemBackground = Color.Parse("#16203a");
	private static readonly Color DiffBorder = Color.Parse("#181e29");

	private readonly List<LineHunk> _hunks = [];
	private readonly List<LineMove> _moves = [];
	private readonly Dictionary<int, (int Move, bool Original)> _moveZones = [];
	private readonly List<int> _trackOf = [];
	private readonly List<int> _moveOrder = [];
	private const float MoveLabelHeight = 18f;
	private static readonly Color MoveBorder = Color.Parse("#8b8b8b9c");
	private static readonly Color MoveActiveBorder = Color.Parse("#FFA500");
	private readonly List<int> _originalZones = [];
	private readonly List<int> _modifiedZones = [];
	private readonly List<Region> _regions = [];
	private List<(int Start, int Count)>? _hiddenOverride;
	private bool _regionsInitialized;
	private readonly DiffMarks _originalMarks = new(removed: true);
	private readonly DiffMarks _modifiedMarks = new(removed: false);
	private readonly List<(float Left, float Top, float Right, float Bottom, int Hunk, bool Stage)> _buttons = [];

	private int _version = -1;
	private float _lineHeight;
	private float _scale = 1f;
	private bool _dirty = true;

	private float _x;
	private float _y;
	private float _height;
	private float _originalWidth;
	private float _gutterLeft;
	private float _overviewLeft;

	private readonly record struct Region(int OriginalStart, int ModifiedStart, int Count, int HideStart, int HideCount, int Key, int OriginalZone, int ModifiedZone);

	public DiffEditor(TextView modified, string originalText, string? originalPath, bool canStage) {
		Modified = modified;
		Original = new TextView(new TextBuffer(originalText), originalPath) {
			CaretVisible = false,
			ShowVerticalScrollbar = false,
			DiffMarks = _originalMarks,
		};
		Original.Buffer.ReadOnly = true;
		Original.Highlighter.SetLanguage(Syntax.Languages.GrammarForPath(originalPath));
		modified.DiffMarks = _modifiedMarks;
		modified.CodeLensEnabled = false;
		CanStage = canStage;
	}

	public TextView Original { get; }

	public TextView Modified { get; }

	public bool CanStage { get; }

	public bool HideUnchangedRegions { get; set; } = true;

	public bool ShowMoves { get; set; }

	public IReadOnlyList<LineHunk> Hunks => _hunks;

	public Action<LineHunk>? StageHunk { get; set; }

	public float ModifiedLeft { get; private set; }

	public bool GutterHovered { get; private set; }

	public void Detach() {
		RemoveZones();
		Modified.DiffMarks = null;
		Modified.CodeLensEnabled = true;
	}

	public void Invalidate() => _dirty = true;

	public bool OriginalFocused { get; private set; }

	public TextView Focused => OriginalFocused ? Original : Modified;

	public void Focus(bool original) {
		OriginalFocused = original;
		Original.CaretVisible = original;
	}

	private float _syncedScroll = -1f;

	public bool Inline { get; private set; }

	public void SetInline(bool inline) {
		if (inline == Inline) return;

		Inline = inline;

		if (inline) {
			RemoveZones();
			Modified.DiffMarks = null;
			Modified.DiffBase = Original.Buffer.GetText();
			return;
		}

		Modified.DiffBase = null;
		Modified.DiffMarks = _modifiedMarks;
		_dirty = true;
	}

	public void Sync(float lineHeight, float scale) {
		if (!_dirty && _version == Modified.Buffer.Version && _lineHeight == lineHeight && _scale == scale) return;

		_dirty = false;
		_version = Modified.Buffer.Version;
		_lineHeight = lineHeight;
		_scale = scale;

		List<(int Start, int Count)>? previousHidden = _hiddenOverride ?? (_regionsInitialized ? CaptureHidden() : null);
		_hiddenOverride = null;

		RemoveZones();
		_hunks.Clear();
		_originalMarks.Clear();
		_modifiedMarks.Clear();
		_regions.Clear();

		TextSnapshot original = Original.Snapshot;
		TextSnapshot modified = Modified.Snapshot;

		string originalText = Original.Buffer.GetText();
		string modifiedText = Modified.Buffer.GetText();
		_hunks.AddRange(LineDiff.Compute(originalText, modifiedText));
		_moves.Clear();
		_moveZones.Clear();

		if (ShowMoves && _hunks.Count > 1) _moves.AddRange(MoveDetector.Compute(_hunks, SplitLines(originalText), SplitLines(modifiedText)));

		float label = MoveLabelHeight * scale;

		for (int i = 0; i < _moves.Count; i++) {
			LineMove move = _moves[i];
			int originalZone = Original.AddZone(move.OriginalStart - 1, label, 0, TextView.ZoneKind.Moved);
			int modifiedZone = Modified.AddZone(move.ModifiedStart - 1, label, 0, TextView.ZoneKind.Moved);
			_originalZones.Add(originalZone);
			_modifiedZones.Add(modifiedZone);
			_moveZones[originalZone] = (i, true);
			_moveZones[modifiedZone] = (i, false);
		}

		foreach (LineHunk hunk in _hunks) {
			_originalMarks.AddLines(hunk.OriginalStart, hunk.OriginalCount);
			_modifiedMarks.AddLines(hunk.ModifiedStart, hunk.ModifiedCount);

			if (hunk.OriginalCount > 0 && hunk.ModifiedCount > 0) {
				string left = LinesText(original, hunk.OriginalStart, hunk.OriginalCount);
				string right = LinesText(modified, hunk.ModifiedStart, hunk.ModifiedCount);
				CharDiff.Compute(left, hunk.OriginalStart, right, hunk.ModifiedStart, _originalMarks.Chars, _modifiedMarks.Chars);
			}

			float extraOriginal = 0f;
			float extraModified = 0f;

			foreach (LineMove move in _moves) {
				if (move.OriginalStart >= hunk.OriginalStart && move.OriginalStart < hunk.OriginalStart + hunk.OriginalCount) extraOriginal += label;
				if (move.ModifiedStart >= hunk.ModifiedStart && move.ModifiedStart < hunk.ModifiedStart + hunk.ModifiedCount) extraModified += label;
			}

			float difference = ((hunk.ModifiedCount - hunk.OriginalCount) * lineHeight) + extraModified - extraOriginal;

			if (difference > 0f) _originalZones.Add(Original.AddZone(hunk.OriginalStart + hunk.OriginalCount - 1, difference, 0, TextView.ZoneKind.Diagonal));
			else if (difference < 0f) _modifiedZones.Add(Modified.AddZone(hunk.ModifiedStart + hunk.ModifiedCount - 1, -difference, 0, TextView.ZoneKind.Diagonal));
		}

		_originalMarks.SortChars();
		_modifiedMarks.SortChars();

		if (HideUnchangedRegions) {
			BuildRegions(original.LineCount, modified.LineCount, previousHidden);
			_regionsInitialized = true;
		}
	}

	private List<(int Start, int Count)> CaptureHidden() {
		List<(int Start, int Count)> hidden = [];

		foreach (TextView.ViewZone zone in Modified.Zones) {
			if (zone.Kind == TextView.ZoneKind.Hidden && zone.Hide > 0) hidden.Add((zone.AfterLine + 1, zone.Hide));
		}

		return hidden;
	}

	private void BuildRegions(int originalLines, int modifiedLines, List<(int Start, int Count)>? previousHidden) {
		int originalStart = 0;
		int modifiedStart = 0;

		for (int i = 0; i <= _hunks.Count; i++) {
			int modifiedEnd = i < _hunks.Count ? _hunks[i].ModifiedStart : modifiedLines;
			int count = modifiedEnd - modifiedStart;

			if (i == _hunks.Count) count = Math.Min(count, originalLines - originalStart);

			AddRegion(originalStart, modifiedStart, count, modifiedStart == 0 && originalStart == 0, i == _hunks.Count, previousHidden);

			if (i < _hunks.Count) {
				originalStart = _hunks[i].OriginalStart + _hunks[i].OriginalCount;
				modifiedStart = _hunks[i].ModifiedStart + _hunks[i].ModifiedCount;
			}
		}
	}

	private void AddRegion(int originalStart, int modifiedStart, int count, bool atStart, bool atEnd, List<(int Start, int Count)>? previousHidden) {
		if (count <= 0) return;

		int hideStart = atStart ? 0 : ContextLines;
		int hideEnd = atEnd ? count : count - ContextLines;

		if (hideEnd - hideStart < MinimumHidden) return;

		if (previousHidden is null) {
			AddHidden(originalStart, modifiedStart, count, hideStart, hideEnd - hideStart);
			return;
		}

		int low = modifiedStart + hideStart;
		int high = modifiedStart + hideEnd;

		foreach ((int start, int length) in previousHidden) {
			int from = Math.Max(low, start);
			int to = Math.Min(high, start + length);

			if (to - from > 0) AddHidden(originalStart, modifiedStart, count, from - modifiedStart, to - from);
		}
	}

	private void AddHidden(int originalStart, int modifiedStart, int count, int hideStart, int hidden) {
		int key = modifiedStart + hideStart;
		float height = HiddenBarHeight * _scale;
		int originalZone = Original.AddZone(originalStart + hideStart - 1, height, hidden, TextView.ZoneKind.Hidden);
		int modifiedZone = Modified.AddZone(modifiedStart + hideStart - 1, height, hidden, TextView.ZoneKind.Hidden);
		_originalZones.Add(originalZone);
		_modifiedZones.Add(modifiedZone);
		_regions.Add(new Region(originalStart, modifiedStart, count, hideStart, hidden, key, originalZone, modifiedZone));
	}

	private static string[] SplitLines(string text) {
		string[] lines = text.Split('\n');

		for (int i = 0; i < lines.Length; i++) {
			if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
		}

		return lines;
	}

	private static string LinesText(TextSnapshot snapshot, int start, int count) {
		int from = snapshot.GetLineSpan(start).Start;
		int to = snapshot.GetLineSpan(Math.Min(snapshot.LineCount - 1, start + count - 1)).End;
		return snapshot.GetText(from, Math.Max(0, to - from));
	}

	private void RemoveZones() {
		foreach (int zone in _originalZones) Original.RemoveZone(zone);
		foreach (int zone in _modifiedZones) Modified.RemoveZone(zone);

		_originalZones.Clear();
		_modifiedZones.Clear();
	}

	public void Render(IPainter painter, float x, float y, float width, float height, float scale) {
		ITextFont font = painter.GetCode();
		_font = font;
		float lineHeight = font.GetLineHeight();
		Original.Scale = scale;
		Sync(lineHeight, scale);

		float overview = OverviewWidth * scale;
		float gutter = GutterWidth * scale;
		float available = Math.Max(0f, width - overview);
		float sash = MathF.Round(available / 2f);

		_x = x;
		_y = y;
		_height = height;
		float movedWidth = MovedLinesWidth(lineHeight) * scale;
		_originalWidth = Math.Max(0f, sash - gutter - movedWidth);
		_gutterLeft = x + _originalWidth + movedWidth;
		ModifiedLeft = sash;
		_overviewLeft = x + available;

		float maximum = Math.Max(0f, Modified.ScrollHeight(lineHeight, height) - height);
		float scroll = Original.ScrollY != _syncedScroll && Modified.ScrollY == _syncedScroll ? Original.ScrollY : Modified.ScrollY;
		_syncedScroll = Math.Clamp(scroll, 0f, maximum);
		Modified.SetScrollY(_syncedScroll);
		Original.SetScrollY(_syncedScroll);

		if (painter.NeedsPaint(x, y, _originalWidth, height)) {
			Original.Render(painter, x, y, _originalWidth, height);
			DrawZones(painter, Original, x, y, _originalWidth, height, lineHeight, false);
			painter.DrawVerticalLine(x + _originalWidth - 1f, y, y + height, DiffBorder);
		}

		Common.Git.IFileDiff? git = Modified.Diff;
		Modified.Diff = null;
		Modified.Render(painter, x + sash, y, available - sash, height);
		Modified.Diff = git;
		DrawZones(painter, Modified, x + sash, y, available - sash, height, lineHeight, true);
		DrawMoves(painter, lineHeight, x + sash, available - sash);
		DrawGutter(painter, gutter, lineHeight);
		DrawOverview(painter, overview, lineHeight);
	}

	private void DrawZones(IPainter painter, TextView view, float x, float y, float width, float height, float lineHeight, bool labels) {
		foreach (TextView.ViewZone zone in view.Zones) {
			if (zone.Kind == TextView.ZoneKind.Space) continue;

			float top = y + view.ZoneTop(zone.Id, lineHeight) - view.ScrollY;

			if (top > y + height || top + zone.Height < y || !painter.NeedsPaint(x, top, width, zone.Height)) continue;

			painter.PushClip(x, Math.Max(y, top), width, Math.Max(0f, Math.Min(y + height, top + zone.Height) - Math.Max(y, top)));

			if (zone.Kind == TextView.ZoneKind.Moved) DrawMoveLabel(painter, zone, x, top, width);
			else if (zone.Kind == TextView.ZoneKind.Diagonal) DrawDiagonal(painter, x, top, width, zone.Height);
			else DrawHiddenBar(painter, view, zone, x, top, width, labels);

			painter.PopClip();

			if (zone.Id != _hoverZone) continue;

			if (_hoverPart == BarPart.Top) painter.FillRectangle(x, top - (2f * _scale), width, 4f * _scale, Theme.FocusBorder);
			else if (_hoverPart == BarPart.Bottom) painter.FillRectangle(x, top + (22f * _scale), width, 4f * _scale, Theme.FocusBorder);
		}
	}

	private int ActiveMove() {
		int caret = Modified.CaretLine;

		for (int i = 0; i < _moves.Count; i++) {
			if (caret >= _moves[i].ModifiedStart && caret < _moves[i].ModifiedStart + _moves[i].ModifiedCount) return i;
		}

		return -1;
	}

	private void DrawMoveLabel(IPainter painter, TextView.ViewZone zone, float x, float top, float width) {
		if (!_moveZones.TryGetValue(zone.Id, out (int Move, bool Original) entry)) return;

		LineMove move = _moves[entry.Move];
		string text = entry.Original
			? "Code moved to line " + (move.ModifiedStart + 1) + "-" + (move.ModifiedStart + move.ModifiedCount)
			: "Code moved from line " + (move.OriginalStart + 1) + "-" + (move.OriginalStart + move.OriginalCount);

		ITextFont ui = painter.GetUi();
		float right = x + width - ((entry.Original ? 4f : 14f) * _scale);
		float baseline = top + ((zone.Height - (4f * _scale) - ui.GetLineHeight()) / 2f) + ui.GetBaseline();
		painter.DrawText(ui, text, right - ui.Measure(text), baseline, Theme.DisabledForeground);
	}

	private float MovedLinesWidth(float lineHeight) {
		_trackOf.Clear();
		_moveOrder.Clear();

		if (_moves.Count == 0) return 0f;

		for (int i = 0; i < _moves.Count; i++) _moveOrder.Add(i);

		_moveOrder.Sort((a, b) => {
			(float fromA, float toA) = MoveEnds(_moves[a], lineHeight);
			(float fromB, float toB) = MoveEnds(_moves[b], lineHeight);
			bool downA = fromA > toA;
			bool downB = fromB > toB;

			if (downA != downB) return downA ? 1 : -1;

			return (downA ? fromA : -toA).CompareTo(downB ? fromB : -toB);
		});

		List<List<(float Top, float Bottom)>> tracks = [];

		foreach (int index in _moveOrder) {
			(float from, float to) = MoveEnds(_moves[index], lineHeight);
			float top = MathF.Min(from, to);
			float bottom = MathF.Max(from, to);
			int track = tracks.FindIndex(set => !set.Exists(range => range.Top < bottom && top < range.Bottom));

			if (track < 0) {
				if (tracks.Count >= 6) {
					track = 0;
				}
				else {
					track = tracks.Count;
					tracks.Add([]);
				}
			}

			tracks[track].Add((top, bottom));
			_trackOf.Add(track);
		}

		return ((tracks.Count - 1) * 10f) + 20f;
	}

	private (float From, float To) MoveEnds(LineMove move, float lineHeight) {
		float from = (Original.TopOf(move.OriginalStart, lineHeight) + Original.TopOf(move.OriginalStart + move.OriginalCount, lineHeight)) / 2f;
		float to = (Modified.TopOf(move.ModifiedStart, lineHeight) + Modified.TopOf(move.ModifiedStart + move.ModifiedCount, lineHeight)) / 2f;
		return (from, to);
	}

	private void DrawMoves(IPainter painter, float lineHeight, float modifiedX, float modifiedWidth) {
		if (_moves.Count == 0) return;

		int active = ActiveMove();
		float stroke = 2f * _scale;
		float padding = 4f * _scale;
		ITextFont code = painter.GetCode();
		float originalGutter = Original.GutterWidth(code);
		float modifiedGutter = Modified.GutterWidth(code);

		for (int i = 0; i < _moves.Count; i++) {
			LineMove move = _moves[i];
			Color color = i == active ? MoveActiveBorder : MoveBorder;

			StrokeBlock(painter, Original, _x + originalGutter - padding, _x + _originalWidth, move.OriginalStart, move.OriginalCount, lineHeight, padding, stroke, color);
			StrokeBlock(painter, Modified, modifiedX + modifiedGutter - padding, modifiedX + modifiedWidth - (10f * _scale), move.ModifiedStart, move.ModifiedCount, lineHeight, padding, stroke, color);
		}

		float left = _x + _originalWidth;
		float right = modifiedX + modifiedGutter - padding;
		float arrow = 15f * _scale;
		float rectangleWidth = modifiedGutter - (16f * _scale);

		painter.PushClip(_x, _y, modifiedX + modifiedWidth - _x, _height);

		for (int order = 0; order < _moveOrder.Count; order++) {
			int index = _moveOrder[order];
			(float from, float to) = MoveEnds(_moves[index], lineHeight);
			from = _y + from - Original.ScrollY;
			to = _y + to - Modified.ScrollY;

			float vertical = left + ((10f + (_trackOf[order] * 10f)) * _scale);
			Color color = index == active ? MoveActiveBorder : MoveBorder;

			painter.FillRectangle(right - rectangleWidth, to - (9f * _scale), rectangleWidth, 18f * _scale, Theme.EditorBackground);

			ReadOnlySpan<System.Numerics.Vector2> path = [new(left, from), new(vertical, from), new(vertical, to), new(right - arrow, to)];
			painter.StrokePolyline(path, stroke, color);

			ReadOnlySpan<System.Numerics.Vector2> head = [new(right - arrow, to - (arrow / 2f)), new(right, to), new(right - arrow, to + (arrow / 2f))];
			painter.FillPolygon(head, color);
		}

		painter.PopClip();
	}

	private void StrokeBlock(IPainter painter, TextView view, float left, float right, int start, int count, float lineHeight, float padding, float stroke, Color color) {
		float top = _y + view.TopOf(start, lineHeight) - view.ScrollY - padding;
		float bottom = _y + view.TopOf(start + count - 1, lineHeight) + lineHeight - view.ScrollY + padding;

		if (bottom < _y || top > _y + _height) return;

		painter.StrokeRoundedRectangle(left + (stroke / 2f), top + (stroke / 2f), right - left - stroke, bottom - top - stroke, 0f, stroke, color);
	}

	private void DrawDiagonal(IPainter painter, float x, float top, float width, float height) {
		float step = 4f * _scale;
		float thickness = MathF.Max(1f, _scale);

		for (float offset = -height; offset < width; offset += step) painter.StrokeLine(x + offset, top + height, x + offset + height, top, thickness, DiagonalFill);
	}

	private void DrawHiddenBar(IPainter painter, TextView view, TextView.ViewZone zone, float x, float top, float width, bool labels) {
		painter.FillRectangle(x, top, width, zone.Height, UnchangedRegionBackground);

		if (!labels) return;

		ITextFont ui = painter.GetUi();
		float gutter = view.GutterWidth(painter.GetCode());
		float middle = top + (zone.Height / 2f);

		painter.DrawIcon(Codicon.Unfold, x + (gutter / 2f), middle, 16f * _scale, zone.Id == _hoverZone && _hoverPart == BarPart.Unfold ? Theme.EditorLinkActiveForeground : UnchangedRegionForeground);

		string text = zone.Hide + " hidden lines";
		painter.DrawText(ui, text, x + gutter, middle - (ui.GetLineHeight() / 2f) + ui.GetBaseline(), UnchangedRegionForeground);
	}

	private void DrawGutter(IPainter painter, float gutter, float lineHeight) {
		if (!painter.NeedsPaint(_gutterLeft, _y, gutter, _height)) return;

		_buttons.Clear();

		painter.FillRectangle(_gutterLeft, _y, gutter, _height, Theme.EditorBackground);
		painter.PushClip(_gutterLeft, _y, gutter, _height);

		int caretLine = Modified.CaretLine;
		float line = 2f * _scale;
		float centre = _gutterLeft + (gutter / 2f);

		for (int i = 0; i < _hunks.Count; i++) {
			(float top, float bottom) = HunkExtent(_hunks[i], lineHeight);

			if (bottom < _y || top > _y + _height) continue;

			LineHunk hunk = _hunks[i];
			bool current = caretLine >= hunk.ModifiedStart && caretLine < hunk.ModifiedStart + Math.Max(1, hunk.ModifiedCount);

			if (!GutterHovered && !current) continue;

			painter.FillRectangle(centre - (line / 2f), top, line, Math.Max(0f, bottom - top), GutterLine);

			int buttons = CanStage ? 2 : 1;
			float buttonsHeight = buttons * ButtonHeight * _scale;
			float buttonTop = ButtonTop(top, bottom, buttonsHeight);
			float buttonLeft = centre - (ButtonWidth * _scale / 2f);

			painter.FillRoundedRectangle(buttonLeft, buttonTop, ButtonWidth * _scale, buttonsHeight, 4f * _scale, GutterItemBackground);
			DrawButton(painter, Codicon.ArrowRight, buttonLeft, buttonTop, i, false);

			if (CanStage) DrawButton(painter, Codicon.Add, buttonLeft, buttonTop + (ButtonHeight * _scale), i, true);
		}

		painter.PopClip();
	}

	private float ButtonTop(float top, float bottom, float itemHeight) {
		float middle = top + ((bottom - top) / 2f) - (itemHeight / 2f);
		float margin = itemHeight;
		float viewTop = _y + margin;
		float viewBottom = _y + _height - margin - itemHeight;
		float parentTop = top + margin;
		float parentBottom = bottom - itemHeight - margin;

		if (viewTop < viewBottom && parentTop < parentBottom) {
			middle = Math.Clamp(middle, viewTop, viewBottom);
			middle = Math.Clamp(middle, parentTop, parentBottom);
		}

		return middle;
	}

	private (float X, float Y) _mouse = (-1f, -1f);

	private void DrawButton(IPainter painter, Codicon icon, float left, float top, int hunk, bool stage) {
		float width = ButtonWidth * _scale;
		float height = ButtonHeight * _scale;

		if (_mouse.X >= left && _mouse.X < left + width && _mouse.Y >= top && _mouse.Y < top + height) painter.FillRoundedRectangle(left, top, width, height, 4f * _scale, Theme.ToolbarHoverBackground);

		painter.DrawIcon(icon, left + (width / 2f), top + (height / 2f), 16f * _scale, Theme.EditorForeground);
		_buttons.Add((left, top, left + width, top + height, hunk, stage));
	}

	private (float Top, float Bottom) HunkExtent(LineHunk hunk, float lineHeight) {
		float bottom = _y + Modified.TopOf(hunk.ModifiedStart + hunk.ModifiedCount, lineHeight) - Modified.ScrollY;
		float originalBottom = _y + Original.TopOf(hunk.OriginalStart + hunk.OriginalCount, lineHeight) - Original.ScrollY;
		float top = _y + Modified.TopOf(hunk.ModifiedStart, lineHeight) - Modified.ScrollY;

		if (hunk.ModifiedCount == 0) top = bottom - (hunk.OriginalCount * lineHeight);

		return (top, Math.Max(bottom, originalBottom));
	}

	private void DrawOverview(IPainter painter, float overview, float lineHeight) {
		if (!painter.NeedsPaint(_overviewLeft, _y, overview, _height)) return;

		painter.FillRectangle(_overviewLeft, _y, overview, _height, Theme.EditorBackground);

		float content = Modified.ScrollHeight(lineHeight, _height);

		if (content <= 0f) return;

		float ratio = _height / Math.Max(content, _height);
		float column = overview / 2f;
		float minimum = 2f * _scale;

		foreach (LineHunk hunk in _hunks) {
			if (hunk.OriginalCount > 0) {
				float top = Original.TopOf(hunk.OriginalStart, lineHeight) * ratio;
				float size = MathF.Max(minimum, hunk.OriginalCount * lineHeight * ratio);
				painter.FillRectangle(_overviewLeft, _y + top, column, size, Theme.DiffRemovedLineBackground);
			}

			if (hunk.ModifiedCount > 0) {
				float top = Modified.TopOf(hunk.ModifiedStart, lineHeight) * ratio;
				float size = MathF.Max(minimum, hunk.ModifiedCount * lineHeight * ratio);
				painter.FillRectangle(_overviewLeft + column, _y + top, column, size, Theme.DiffInsertedLineBackground);
			}
		}

		float thumbTop = Modified.ScrollY * ratio;
		float thumbHeight = MathF.Min(_height, _height * ratio);
		painter.FillRectangle(_overviewLeft, _y + thumbTop, overview, thumbHeight, Theme.ScrollbarSlider);
	}

	public bool IsInOriginal(float absoluteX) => absoluteX >= _x && absoluteX < _x + _originalWidth;

	public bool IsInModified(float absoluteX) => absoluteX >= _x + ModifiedLeft && absoluteX < _overviewLeft;

	private enum BarPart : byte
	{
		None,
		Top,
		Unfold,
		Text,
		Bottom,
	}

	private int _hoverZone = -1;
	private BarPart _hoverPart;
	private ITextFont? _font;

	public CursorCode? Cursor => _hoverPart switch {
		BarPart.Unfold => CursorCode.Hand,
		BarPart.Top or BarPart.Bottom => CursorCode.SizeNorthSouth,
		BarPart.Text => CursorCode.Arrow,
		_ => _buttonHovered ? CursorCode.Hand : null,
	};

	private bool _buttonHovered;

	private BarPart HitBar(float absoluteX, float absoluteY, out int zoneId) {
		zoneId = -1;

		if (_font is not { } font) return BarPart.None;

		bool inOriginal = absoluteX >= _x && absoluteX < _x + _originalWidth;

		if (!inOriginal && !IsInModified(absoluteX)) return BarPart.None;

		TextView view = inOriginal ? Original : Modified;
		float lineHeight = font.GetLineHeight();

		foreach (TextView.ViewZone zone in view.Zones) {
			if (zone.Kind != TextView.ZoneKind.Hidden) continue;

			float top = _y + view.ZoneTop(zone.Id, lineHeight) - view.ScrollY;
			float offset = (absoluteY - top) / _scale;

			if (offset < -6f || offset >= 28f) continue;

			zoneId = zone.Id;

			if (offset < 4f) return BarPart.Top;

			if (offset >= 18f) return BarPart.Bottom;

			float localX = absoluteX - (inOriginal ? _x : _x + ModifiedLeft);
			return !inOriginal && localX < view.GutterWidth(font) ? BarPart.Unfold : BarPart.Text;
		}

		return BarPart.None;
	}

	public bool Hover(float absoluteX, float absoluteY) {
		bool hovered = absoluteX >= _gutterLeft && absoluteX < _gutterLeft + (GutterWidth * _scale) && absoluteY >= _y && absoluteY < _y + _height;
		bool changed = hovered != GutterHovered || (hovered && _mouse != (absoluteX, absoluteY));

		GutterHovered = hovered;
		_mouse = hovered ? (absoluteX, absoluteY) : (-1f, -1f);
		_buttonHovered = false;

		foreach ((float left, float top, float right, float bottom, _, _) in _buttons) {
			if (absoluteX >= left && absoluteX < right && absoluteY >= top && absoluteY < bottom) _buttonHovered = true;
		}

		BarPart part = HitBar(absoluteX, absoluteY, out int zone);

		if (part != _hoverPart || zone != _hoverZone) {
			_hoverPart = part;
			_hoverZone = zone;
			changed = true;
		}

		return changed;
	}

	public bool Press(ITextFont font, float absoluteX, float absoluteY, int clicks, float viewportHeight) {
		float lineHeight = font.GetLineHeight();

		foreach ((float left, float top, float right, float bottom, int hunk, bool stage) in _buttons) {
			if (absoluteX < left || absoluteX >= right || absoluteY < top || absoluteY >= bottom || hunk >= _hunks.Count) continue;

			if (stage) StageHunk?.Invoke(_hunks[hunk]);
			else RevertHunk(_hunks[hunk]);

			return true;
		}

		if (absoluteX >= _overviewLeft) {
			float content = Modified.ScrollHeight(lineHeight, viewportHeight);
			float ratio = Math.Max(content, _height) / _height;
			Modified.SetScrollY(Math.Clamp(((absoluteY - _y) * ratio) - (viewportHeight / 2f), 0f, Math.Max(0f, content - viewportHeight)));
			return true;
		}

		bool inOriginal = absoluteX >= _x && absoluteX < _x + _originalWidth;

		if (!inOriginal && !IsInModified(absoluteX)) return absoluteX >= _gutterLeft && absoluteX < _overviewLeft;

		BarPart part = HitBar(absoluteX, absoluteY, out int zoneId);

		if (part != BarPart.None) {
			if (RegionFor(zoneId) is not { } region) return true;

			int start = region.ModifiedStart + region.HideStart;
			int length = region.HideCount;

			if (part == BarPart.Unfold || (part == BarPart.Text && clicks >= 2)) length = 0;
			else if (part == BarPart.Top) {
				int reveal = Math.Min(RevealLines, length);
				start += reveal;
				length -= reveal;
			}
			else if (part == BarPart.Bottom) length -= Math.Min(RevealLines, length);
			else return true;

			List<(int Start, int Count)> hidden = CaptureHidden();
			hidden.RemoveAll(entry => entry.Start == region.ModifiedStart + region.HideStart);

			if (length > 0) hidden.Add((start, length));

			hidden.Sort(static (a, b) => a.Start.CompareTo(b.Start));
			_hiddenOverride = hidden;
			_hoverPart = BarPart.None;
			_hoverZone = -1;
			_dirty = true;
			return true;
		}

		Focus(inOriginal);
		return false;
	}

	private Region? RegionFor(int zone) {
		foreach (Region region in _regions) {
			if (region.ModifiedZone == zone || region.OriginalZone == zone) return region;
		}

		return null;
	}

	public void ShowAllRegions() {
		_hiddenOverride = [];
		_dirty = true;
	}

	public void CollapseAllRegions() {
		_hiddenOverride = null;
		_regionsInitialized = false;
		_dirty = true;
	}

	private void RevertHunk(LineHunk hunk) {
		(int start, int end, string replacement) = Splice(Modified.Snapshot, hunk.ModifiedStart, hunk.ModifiedCount, Original.Snapshot, hunk.OriginalStart, hunk.OriginalCount);

		Modified.Buffer.PushStackElement();
		Modified.Buffer.Replace(start, end - start, replacement);
		Modified.Buffer.PushStackElement();
		_dirty = true;
	}

	public string OriginalWithHunk(LineHunk hunk) {
		TextSnapshot original = Original.Snapshot;
		(int start, int end, string replacement) = Splice(original, hunk.OriginalStart, hunk.OriginalCount, Modified.Snapshot, hunk.ModifiedStart, hunk.ModifiedCount);
		string text = Original.Buffer.GetText();
		return string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));
	}

	public void ReplaceOriginal(string text) {
		Original.Buffer.ReadOnly = false;
		Original.Buffer.ReplaceChanged(Original.Buffer.GetText(), text);
		Original.Buffer.ReadOnly = true;
		_dirty = true;
	}

	private static (int Start, int End, string Replacement) Splice(TextSnapshot target, int targetStart, int targetCount, TextSnapshot source, int sourceStart, int sourceCount) {
		string replacement = sourceCount > 0 ? LinesText(source, sourceStart, sourceCount) : string.Empty;
		string newline = Newline(target);

		if (targetCount > 0) {
			int start = target.GetLineSpan(targetStart).Start;
			int end = target.GetLineSpan(targetStart + targetCount - 1).End;

			if (sourceCount == 0) {
				if (targetStart + targetCount < target.LineCount) end = target.GetLineSpan(targetStart + targetCount).Start;
				else if (start > 0) start = target.GetLineSpan(targetStart - 1).End;
			}

			return (start, end, replacement);
		}

		if (targetStart < target.LineCount) {
			int at = target.GetLineSpan(targetStart).Start;
			return (at, at, replacement + newline);
		}

		return (target.Length, target.Length, (target.Length > 0 ? newline : string.Empty) + replacement);
	}

	private static string Newline(TextSnapshot snapshot) {
		if (snapshot.LineCount < 2) return Environment.NewLine;

		TextSpan first = snapshot.GetLineSpan(0);
		return snapshot.GetText(first.End, Math.Min(2, snapshot.GetLineSpan(1).Start - first.End)) is { Length: > 0 } newline ? newline : "\n";
	}
}
