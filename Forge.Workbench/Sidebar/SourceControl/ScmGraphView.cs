using System.Numerics;

using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Git;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ScmGraphView : Panel
{
	private const float RowHeight = 22f;
	private const float LaneWidth = 11f;
	private const float CurveRadius = 5f;
	private const float CircleRadius = 4f;
	private const float CircleStroke = 2f;
	private const float Twistie = 16f;
	private const float Indent = 8f;
	private const float BadgeHeight = 18f;
	private const float BadgeGap = 4f;
	private const float BadgeDescriptionMax = 100f;
	private const float PaddingRight = 12f;
	private const float DecorationWidth = 16f;

	private static readonly Color BadgeForeground = Theme.PanelBackground;
	private static readonly Color DefaultBadgeBackground = Theme.BadgeBackground;

	private enum RowKind
	{
		Commit,
		Change,
		LoadMore,
	}

	private readonly record struct Row(RowKind Kind, int Item, GitCommitChange Change);

	private readonly HashSet<string> _expanded = [with(StringComparer.Ordinal)];
	private readonly Dictionary<string, IReadOnlyList<GitCommitChange>> _changes = [with(StringComparer.Ordinal)];
	private readonly List<Row> _rows = [];
	private readonly System.Collections.Concurrent.ConcurrentQueue<(string Id, IReadOnlyList<GitCommitChange> Changes)> _loadedChanges = new();

	private List<ScmGraphItem> _items = [];
	private GitHistory? _history;
	private volatile GitHistory? _loaded;
	private volatile bool _stale = true;
	private int _loading;
	private int _limit;
	private float _scrollY;
	private int _hover = -1;
	private int _selected = -1;
	private string? _selectedKey;

	public ScmGraphView() {
		Name = "ScmGraph";
		Dock = DockType.Fill;
	}

	public int PageSize { get; set; } = 50;

	public Action? Changed { get; set; }

	public Action<GitCommit, GitCommitChange>? ChangeOpened { get; set; }

	public Action<string, string, string>? ChangesOpened { get; set; }

	private void OpenChanges(GitCommit commit) {
		if (_history is not { } history) return;

		if (commit.Id == ScmGraph.IncomingId) {
			if (history.MergeBase is { } mergeBase && history.RemoteRevision is { } remote) ChangesOpened?.Invoke($"{commit.Subject} - {history.Current?.Name} ↔ {history.Remote?.Name}", mergeBase, remote);

			return;
		}

		if (commit.Id == ScmGraph.OutgoingId) {
			if (history.MergeBase is { } mergeBase && history.CurrentRevision is { } head) ChangesOpened?.Invoke($"{commit.Subject} - {history.Remote?.Name} ↔ {history.Current?.Name}", mergeBase, head);

			return;
		}

		ChangesOpened?.Invoke($"{commit.ShortId} - {commit.Subject}", commit.Parents.Length > 0 ? commit.Parents[0] : GitHistoryReader.EmptyTree, commit.Id);
	}

	public IGitRepository? Repository {
		get;
		set {
			if (ReferenceEquals(field, value)) return;

			field = value;
			value?.OnChanged(MarkStale);
			_limit = 0;
			_changes.Clear();
			MarkStale();
		}
	}

	public void MarkStale() => _stale = true;

	public void Refresh() {
		_changes.Clear();
		MarkStale();
	}

	public void SyncIfStale() {
		if (_loaded is { } loaded) {
			_loaded = null;
			Apply(loaded);
		}

		if (!_loadedChanges.IsEmpty) {
			while (_loadedChanges.TryDequeue(out (string Id, IReadOnlyList<GitCommitChange> Changes) entry)) _changes[entry.Id] = entry.Changes;

			Rebuild();
			Repaint();
		}

		if (!_stale || !IsShowing) return;

		_stale = false;
		Load();
	}

	private void Load() {
		if (Repository is not { } repository) {
			Apply(new GitHistory([], null, null, null, null, null, false));
			return;
		}

		if (Interlocked.Exchange(ref _loading, 1) == 1) {
			_stale = true;
			return;
		}

		string root = repository.GetRootPath();
		int limit = Math.Max(_limit, Math.Clamp(PageSize, 1, 1000));
		_limit = limit;

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			GitHistory? history = GitHistoryReader.Load(root, limit);
			Volatile.Write(ref _loading, 0);

			if (history is null) return;

			_loaded = history;
			Changed?.Invoke();
		}, null);
	}

	private void Apply(GitHistory history) {
		_history = history;
		_items = ScmGraph.Build(history);
		Rebuild();
		Repaint();
	}

	private void Rebuild() {
		_rows.Clear();

		for (int i = 0; i < _items.Count; i++) {
			_rows.Add(new Row(RowKind.Commit, i, default));

			string id = _items[i].Commit.Id;

			if (!_expanded.Contains(id) || !_changes.TryGetValue(id, out IReadOnlyList<GitCommitChange>? changes)) continue;

			foreach (GitCommitChange change in changes) _rows.Add(new Row(RowKind.Change, i, change));
		}

		if (_history is { HasMore: true }) _rows.Add(new Row(RowKind.LoadMore, -1, default));

		_selected = _selectedKey is null ? -1 : _rows.FindIndex(row => KeyOf(row) == _selectedKey);
		ClampScroll();
	}

	private string KeyOf(in Row row) => row.Kind switch {
		RowKind.Commit => _items[row.Item].Commit.Id,
		RowKind.Change => _items[row.Item].Commit.Id + ":" + row.Change.Path,
		_ => "load-more",
	};

	private bool IsSynthetic(GitCommit commit) => commit.Id is ScmGraph.IncomingId or ScmGraph.OutgoingId;

	private void Toggle(int item) {
		GitCommit commit = _items[item].Commit;

		if (IsSynthetic(commit)) {
			OpenChanges(commit);
			return;
		}

		if (!_expanded.Remove(commit.Id)) {
			_expanded.Add(commit.Id);

			if (!_changes.ContainsKey(commit.Id) && Repository is { } repository) {
				string root = repository.GetRootPath();

				ThreadPool.UnsafeQueueUserWorkItem(_ => {
					_loadedChanges.Enqueue((commit.Id, GitHistoryReader.Changes(root, commit)));
					Changed?.Invoke();
				}, null);
			}
		}

		Rebuild();
		Repaint();
	}

	#region Scrolling

	private float ContentHeight => _rows.Count * RowHeight * Scale;

	private void ClampScroll() => _scrollY = Math.Clamp(_scrollY, 0f, Math.Max(0f, ContentHeight - Tall));

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float previous = _scrollY;
		_scrollY -= delta / 120f * 3f * RowHeight * Scale;
		ClampScroll();

		if (_scrollY != previous) Repaint();

		return true;
	}

	public override void PerformLayout() {
		ClampScroll();
		base.PerformLayout();
	}

	#endregion

	#region Input

	private int RowAt(float y) {
		int row = (int)((y + _scrollY) / (RowHeight * Scale));
		return y >= 0f && row < _rows.Count ? row : -1;
	}

	public override void OnCursorMoved(float x, float y) {
		int row = RowAt(y);

		if (row == _hover) return;

		RepaintRow(_hover);
		_hover = row;
		RepaintRow(_hover);
	}

	public override void OnCursorExited() {
		RepaintRow(_hover);
		_hover = -1;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		RequestFocus();

		int index = RowAt(y);

		if (index < 0 || button != MouseButton.Left) return true;

		Row row = _rows[index];
		_selected = index;
		_selectedKey = KeyOf(row);

		switch (row.Kind) {
			case RowKind.Commit:
				if (x < (Indent + Twistie) * Scale) Toggle(row.Item);
				else OpenChanges(_items[row.Item].Commit);

				Repaint();
				break;

			case RowKind.Change:
				ChangeOpened?.Invoke(_items[row.Item].Commit, row.Change);
				Repaint();
				break;

			default:
				_limit += Math.Clamp(PageSize, 1, 1000);
				MarkStale();
				Changed?.Invoke();
				break;
		}

		return true;
	}

	private void RepaintRow(int row) {
		if (row < 0) return;

		float height = RowHeight * Scale;
		Repaint(0f, (row * height) - _scrollY, Wide, height);
	}

	#endregion

	#region Painting

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float height = RowHeight * scale;

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		int first = Math.Max(0, (int)(_scrollY / height));

		for (int i = first; i < _rows.Count; i++) {
			float top = ScreenY + (i * height) - _scrollY;

			if (top >= ScreenY + Tall) break;

			if (!painter.NeedsPaint(ScreenX, top, Wide, height)) continue;

			PaintRow(painter, i, top, height, scale);
		}

		painter.PopClip();
	}

	private Color RowBackground(int index, IPainter painter, float top, float height) {
		bool selected = index == _selected;

		if (selected) {
			Color color = HasFocus ? Theme.ListActiveSelectionBackground : Theme.ListInactiveSelectionBackground;
			painter.FillRectangle(ScreenX, top, Wide, height, color);
			return color;
		}

		if (index == _hover) {
			painter.FillRectangle(ScreenX, top, Wide, height, Theme.ListHoverBackground);
			return Theme.ListHoverBackground;
		}

		return Theme.SidebarBackground;
	}

	private void PaintRow(IPainter painter, int index, float top, float height, float scale) {
		Row row = _rows[index];
		Color background = RowBackground(index, painter, top, height);
		ITextFont font = painter.GetUi();
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float centreY = top + (height / 2f);
		float right = ScreenX + Wide - (PaddingRight * scale);

		if (row.Kind == RowKind.LoadMore) {
			float graph = ScreenX + ((Indent + Twistie) * scale);
			painter.DrawText(font, "Load More...", graph, baseline, Theme.Foreground);
			return;
		}

		ScmGraphItem item = _items[row.Item];

		if (row.Kind == RowKind.Change) {
			PaintChange(painter, item, row.Change, top, height, scale, font, baseline, right);
			return;
		}

		float left = ScreenX + (Indent * scale);

		if (!IsSynthetic(item.Commit)) painter.DrawIcon(_expanded.Contains(item.Commit.Id) ? Codicon.ChevronDown : Codicon.ChevronRight, left + (Twistie * scale / 2f), centreY, Metrics.IconSize * scale, Theme.Foreground);

		left += Twistie * scale;

		float graphWidth = DrawGraph(painter, item, left, top, scale, background, _expanded.Contains(item.Commit.Id));
		float labelLeft = left + graphWidth;
		float badgesWidth = MeasureBadges(painter, item, scale);
		float badgesLeft = Math.Max(labelLeft, right - badgesWidth);
		bool current = item.Commit.Id == _history?.CurrentRevision;
		ITextFont label = current ? painter.GetUiStrong() : font;

		painter.PushClip(labelLeft, top, Math.Max(0f, badgesLeft - labelLeft - (BadgeGap * scale)), height);
		painter.DrawText(label, item.Commit.Subject, labelLeft, baseline, Theme.Foreground);

		if (item.Commit.Author.Length > 0) painter.DrawText(font, item.Commit.Author, labelLeft + label.Measure(item.Commit.Subject) + (Metrics.Space6 * scale), baseline, Theme.DescriptionForeground);

		painter.PopClip();
		DrawBadges(painter, item, badgesLeft, centreY, scale);
	}

	private void PaintChange(IPainter painter, ScmGraphItem item, GitCommitChange change, float top, float height, float scale, ITextFont font, float baseline, float right) {
		float centreY = top + (height / 2f);
		float lanes = LaneWidth * (Math.Max(item.Output.Count, 1) + 1) * scale;
		float left = ScreenX + ((Indent + Twistie) * scale);

		for (int lane = 0; lane < item.Output.Count; lane++) {
			float x = left + (LaneWidth * (lane + 1) * scale);
			painter.StrokeLine(x, top, x, top + height, scale, item.Output[lane].Color);
		}

		left += lanes;

		float iconSize = Metrics.IconSize * scale;
		string name = Path.GetFileName(change.Path);
		Color color = GitStatusColor.Of(change.Status) ?? Theme.SidebarFileForeground;

		painter.DrawFileIcon(name, left + (iconSize / 2f), centreY, iconSize);
		left += iconSize + (Metrics.Space6 * scale);

		string root = Repository?.GetRootPath() ?? string.Empty;
		string folder = Path.GetDirectoryName(change.Path) is { } directory && directory.Length > root.Length ? directory[(root.Length + 1)..] : string.Empty;
		float labelRight = right - (DecorationWidth * scale);

		painter.PushClip(left, top, Math.Max(0f, labelRight - left), height);
		painter.DrawText(font, name, left, baseline, color);

		if (change.Status == GitFileStatus.Deleted) painter.DrawHorizontalLine(MathF.Round(centreY), left, left + font.Measure(name), color);

		if (folder.Length > 0) painter.DrawText(font, folder, left + font.Measure(name) + (Metrics.Space6 * scale), baseline, Theme.DescriptionForeground);

		painter.PopClip();

		string letter = GitStatusLetter.Of(change.Status);

		if (letter.Length > 0) painter.DrawText(font, letter, right - (DecorationWidth * scale) + (((DecorationWidth * scale) - font.Measure(letter)) / 2f), baseline, color);
	}

	private static Codicon IconOf(GitRefKind kind) => kind switch {
		GitRefKind.Remote => Codicon.Cloud,
		GitRefKind.Tag => Codicon.Tag,
		_ => Codicon.GitBranch,
	};

	private static IEnumerable<(ScmGraphReference First, int Count, bool Description)> Badges(ScmGraphItem item) {
		ReadOnlySpan<ScmGraphReference> references = item.References;
		int start = 0;

		if (references.Length > 0 && references[0].Color is not null) {
			yield return (references[0], 1, true);
			start = 1;
		}

		List<(Color Color, GitRefKind Kind, ScmGraphReference First, int Count)> groups = [];

		for (int i = start; i < item.References.Length; i++) {
			ScmGraphReference reference = item.References[i];

			if (reference.Color is not { } color) continue;

			int existing = groups.FindIndex(group => group.Color.ToArgb() == color.ToArgb() && group.Kind == reference.Ref.Kind);

			if (existing >= 0) groups[existing] = groups[existing] with { Count = groups[existing].Count + 1 };
			else groups.Add((color, reference.Ref.Kind, reference, 1));
		}

		foreach ((_, _, ScmGraphReference first, int count) in groups) yield return (first, count, false);
	}

	private static float BadgeWidth(IPainter painter, ScmGraphReference reference, int count, bool description, float scale) {
		ITextFont small = painter.GetUi(12f);
		float width = (reference.Ref.Kind == GitRefKind.Branch ? 18f : 18f) * scale;

		if (count > 1) width += small.Measure(count.ToString(System.Globalization.CultureInfo.InvariantCulture)) + (4f * scale);

		if (description) width += Math.Min(small.Measure(reference.Ref.Name), BadgeDescriptionMax * scale) + (4f * scale);

		return width;
	}

	private static float MeasureBadges(IPainter painter, ScmGraphItem item, float scale) {
		float width = 0f;

		foreach ((ScmGraphReference first, int count, bool description) in Badges(item)) width += BadgeWidth(painter, first, count, description, scale) + (BadgeGap * scale);

		return width;
	}

	private static void DrawBadges(IPainter painter, ScmGraphItem item, float left, float centreY, float scale) {
		ITextFont small = painter.GetUi(12f);
		float height = BadgeHeight * scale;
		float top = centreY - (height / 2f);
		float baseline = centreY - (small.GetLineHeight() / 2f) + small.GetBaseline();

		foreach ((ScmGraphReference first, int count, bool description) in Badges(item)) {
			float width = BadgeWidth(painter, first, count, description, scale);
			Color background = first.Color ?? DefaultBadgeBackground;
			Color foreground = first.Color is null ? Theme.Foreground : BadgeForeground;
			float x = left;

			painter.FillRoundedRectangle(left, top, width, height, 10f * scale, background);

			if (count > 1) {
				string text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
				x += 4f * scale;
				painter.DrawText(small, text, x, baseline, foreground);
				x += small.Measure(text);
			}

			painter.DrawIcon(IconOf(first.Ref.Kind), x + (9f * scale), centreY, (first.Ref.Kind == GitRefKind.Branch ? 12f : 16f) * scale, foreground);
			x += 18f * scale;

			if (description) {
				painter.PushClip(x, top, BadgeDescriptionMax * scale, height);
				painter.DrawText(small, first.Ref.Name, x, baseline, foreground);
				painter.PopClip();
			}

			left += width + (BadgeGap * scale);
		}
	}

	#endregion

	#region Graph

	private static float DrawGraph(IPainter painter, ScmGraphItem item, float x, float y, float scale, Color background, bool expanded) {
		float w = LaneWidth * scale;
		float h = RowHeight * scale;
		float r = CurveRadius * scale;
		float stroke = scale;
		GitCommit commit = item.Commit;
		List<ScmGraphLane> input = item.Input;
		List<ScmGraphLane> output = item.Output;
		int inputIndex = input.FindIndex(lane => lane.Id == commit.Id);
		int circle = inputIndex >= 0 ? inputIndex : input.Count;
		Color circleColor = circle < output.Count ? output[circle].Color : circle < input.Count ? input[circle].Color : ScmGraph.RefColor;
		Span<Vector2> points = stackalloc Vector2[24];
		int outputIndex = 0;

		for (int index = 0; index < input.Count; index++) {
			Color color = input[index].Color;
			float lane = x + (w * (index + 1));

			if (input[index].Id == commit.Id) {
				if (index != circle) {
					int count = Arc(points, x + (w * index), y, w, 0f, 90f);
					painter.StrokePolyline(points[..count], stroke, color);
					painter.StrokeLine(x + (w * index), y + w, x + (w * (circle + 1)), y + w, stroke, color);
				}
				else outputIndex++;

				continue;
			}

			if (outputIndex >= output.Count || input[index].Id != output[outputIndex].Id) continue;

			if (index == outputIndex) painter.StrokeLine(lane, y, lane, y + h, stroke, color);
			else {
				float target = x + (w * (outputIndex + 1));
				painter.StrokeLine(lane, y, lane, y + (6f * scale), stroke, color);
				int count = Arc(points, lane - r, y + (6f * scale), r, 0f, 90f);
				painter.StrokePolyline(points[..count], stroke, color);
				painter.StrokeLine(lane - r, y + (h / 2f), target + r, y + (h / 2f), stroke, color);
				count = Arc(points, target + r, y + (h / 2f) + r, r, -90f, -180f);
				painter.StrokePolyline(points[..count], stroke, color);
				painter.StrokeLine(target, y + (h / 2f) + r, target, y + h, stroke, color);
			}

			outputIndex++;
		}

		for (int i = 1; i < commit.Parents.Length; i++) {
			int parent = output.FindLastIndex(lane => lane.Id == commit.Parents[i]);

			if (parent < 0) continue;

			Color color = output[parent].Color;
			int count = Arc(points, x + (w * parent), y + h, w, -90f, 0f);
			painter.StrokePolyline(points[..count], stroke, color);
			painter.StrokeLine(x + (w * parent), y + (h / 2f), x + (w * (circle + 1)), y + (h / 2f), stroke, color);
		}

		float cx = x + (w * (circle + 1));
		float cy = y + w;

		if (inputIndex >= 0) painter.StrokeLine(cx, y, cx, y + (h / 2f), stroke, input[inputIndex].Color);

		if (commit.Parents.Length > 0) painter.StrokeLine(cx, y + (h / 2f), cx, y + h, expanded ? 3f * scale : stroke, circleColor);

		switch (item.Kind) {
			case ScmGraphKind.Head:
				painter.FillCircle(cx, cy, (CircleRadius + 4f) * scale, background);
				painter.FillCircle(cx, cy, (CircleRadius + 2f) * scale, circleColor);
				painter.FillCircle(cx, cy, CircleRadius * scale, background);
				break;

			case ScmGraphKind.IncomingChanges or ScmGraphKind.OutgoingChanges:
				painter.FillCircle(cx, cy, (CircleRadius + 4f) * scale, background);
				DashedCircle(painter, cx, cy, (CircleRadius + 1f) * scale, scale, circleColor);
				break;

			default:
				if (commit.Parents.Length > 1) {
					painter.FillCircle(cx, cy, (CircleRadius + 3f) * scale, background);
					painter.FillCircle(cx, cy, (CircleRadius + 1f) * scale, circleColor);
					painter.FillCircle(cx, cy, CircleRadius * scale, background);
					painter.FillCircle(cx, cy, (CircleRadius - 2f) * scale, circleColor);
				}
				else {
					painter.FillCircle(cx, cy, (CircleRadius + 2f) * scale, background);
					painter.FillCircle(cx, cy, CircleRadius * scale, circleColor);
				}

				break;
		}

		return w * (Math.Max(Math.Max(input.Count, output.Count), 1) + 1);
	}

	private static int Arc(Span<Vector2> points, float cx, float cy, float radius, float fromDegrees, float toDegrees) {
		int count = points.Length;

		for (int i = 0; i < count; i++) {
			float angle = (fromDegrees + ((toDegrees - fromDegrees) * i / (count - 1))) * MathF.PI / 180f;
			points[i] = new Vector2(cx + (radius * MathF.Cos(angle)), cy + (radius * MathF.Sin(angle)));
		}

		return count;
	}

	private static void DashedCircle(IPainter painter, float cx, float cy, float radius, float scale, Color color) {
		float circumference = 2f * MathF.PI * radius;
		float dash = 4f * scale;
		float gap = 2f * scale;
		Span<Vector2> points = stackalloc Vector2[6];

		for (float at = 0f; at < circumference; at += dash + gap) {
			float from = at / radius * 180f / MathF.PI;
			float to = Math.Min(at + dash, circumference) / radius * 180f / MathF.PI;
			int count = Arc(points, cx, cy, radius, from, to);
			painter.StrokePolyline(points[..count], scale, color);
		}
	}

	#endregion
}
