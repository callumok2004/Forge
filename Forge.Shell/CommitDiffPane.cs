using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Editor;
using Forge.Git;
using Forge.Text;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Shell;

public sealed class CommitDiffPane : EditorPane
{
	public const string ActiveContext = "commitDiffEditorFocus";

	private const float HeaderHeight = 35f;
	private const float SearchHeight = 34f;
	private const float MinimumFilesWidth = 190f;
	private const float MaximumFilesWidth = 800f;
	private const float DefaultFilesWidth = 320f;
	private const float MinimumContentWidth = 220f;
	private const float RowHeight = 22f;
	private const float SashSize = 4f;
	private const int SearchDebounce = 250;
	private const int ModelCacheSize = 80;
	private const int PrefetchLimit = 80;
	private const int SearchResultLimit = 2000;

	private enum RowKind
	{
		Folder,
		File,
		Match,
	}

	private sealed class Node
	{
		public required RowKind Kind { get; init; }

		public required string Name { get; init; }

		public required string Key { get; init; }

		public int Depth { get; set; }

		public int FileIndex { get; init; } = -1;

		public int Insertions { get; set; }

		public int Deletions { get; set; }

		public int FileCount { get; set; }

		public bool Collapsed { get; set; }

		public SearchMatch? Match { get; init; }

		public List<Node> Children { get; } = [];
	}

	private sealed record SearchMatch(int FileIndex, bool Original, int Line, int Column, int Length, string Text);

	private readonly string _root;
	private readonly string _title;
	private readonly string _from;
	private readonly string _to;
	private readonly IReadOnlyList<GitCommitChange> _changes;
	private readonly Action<string> _openFile;
	private readonly Action _changed;

	private readonly TextEntry _search;
	private readonly List<Node> _tree = [];
	private readonly List<Node> _rows = [];
	private readonly Dictionary<int, (string Original, string Modified)> _texts = [];
	private readonly LinkedList<int> _recent = [];
	private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Original, string Modified)> _prefetched = new();

	private Dictionary<int, List<SearchMatch>>? _matches;
	private volatile Dictionary<int, List<SearchMatch>>? _pendingMatches;
	private volatile bool _searchCapped;
	private string? _searchStatus;
	private long _searchDue;
	private int _searchGeneration;

	private float _filesWidth = DefaultFilesWidth;
	private bool _sashHover;
	private bool _sashDragging;
	private float _scroll;
	private int _hoverRow = -1;
	private int _focusRow = -1;
	private int _selectedFile = -1;

	private TextView? _view;
	private DiffEditor? _diff;
	private bool _selecting;
	private bool _diffSashDragging;
	private SearchMatch? _pendingReveal;

	public CommitDiffPane(string root, string title, string from, string to, IReadOnlyList<GitCommitChange> changes, Action<string> openFile, Action changed) {
		_root = root;
		_title = title;
		_from = from;
		_to = to;
		_changes = changes;
		_openFile = openFile;
		_changed = changed;
		Name = "CommitDiff";
		ContextKey = ActiveContext;
		KeyboardInputEnabled = true;

		_search = new TextEntry { Name = "CommitDiffSearch", Placeholder = "Search changed lines", FontSize = Metrics.FontSizeBody, TextChanged = QueueSearch };
		AddChild(_search);

		BuildTree(null);
		Flatten();

		if (FirstFileRow() is var first and >= 0) {
			_focusRow = first;
			ShowFile(_rows[first].FileIndex, null);
		}

		Prefetch();
	}

	public override string Title => _title;

	public override Codicon Icon => Codicon.GitCommit;

	#region Tree

	private void BuildTree(Dictionary<int, List<SearchMatch>>? matches) {
		_tree.Clear();

		List<int> included = [];

		for (int i = 0; i < _changes.Count; i++) {
			if (matches is null || matches.ContainsKey(i)) included.Add(i);
		}

		string[][] segments = [.. included.Select(index => Relative(_changes[index].Path).Split('/'))];
		int prefix = CommonPrefix(segments);
		Node root = new() { Kind = RowKind.Folder, Name = string.Empty, Key = string.Empty };

		for (int i = 0; i < included.Count; i++) {
			string[] parts = segments[i];
			Node current = root;
			string path = string.Empty;

			for (int s = prefix; s < parts.Length - 1; s++) {
				if (parts[s].Length == 0) continue;

				path = path.Length == 0 ? parts[s] : path + "/" + parts[s];
				Node? next = current.Children.Find(child => child.Kind == RowKind.Folder && child.Name == parts[s]);

				if (next is null) {
					next = new Node { Kind = RowKind.Folder, Name = parts[s], Key = "folder:" + path, Collapsed = matches is null };
					current.Children.Add(next);
				}

				current = next;
			}

			GitCommitChange change = _changes[included[i]];
			Node file = new() { Kind = RowKind.File, Name = Path.GetFileName(change.Path), Key = "file:" + included[i], FileIndex = included[i], Insertions = change.Insertions, Deletions = change.Deletions };

			if (matches is not null && matches.TryGetValue(included[i], out List<SearchMatch>? found)) {
				foreach (SearchMatch match in found) file.Children.Add(new Node { Kind = RowKind.Match, Name = match.Text, Key = "match:" + included[i] + ":" + match.Line + ":" + match.Column + ":" + match.Original, FileIndex = included[i], Match = match });
			}

			current.Children.Add(file);
		}

		Finish(root);
		Compress(root);
		_tree.AddRange(root.Children);
	}

	private static void Finish(Node node) {
		node.Children.Sort(static (left, right) => left.Kind != right.Kind ? (left.Kind == RowKind.Folder ? -1 : 1) : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));

		foreach (Node child in node.Children) {
			if (child.Kind != RowKind.Folder) {
				if (child.Kind == RowKind.File) {
					node.Insertions += child.Insertions;
					node.Deletions += child.Deletions;
					node.FileCount++;
				}

				continue;
			}

			Finish(child);
			node.Insertions += child.Insertions;
			node.Deletions += child.Deletions;
			node.FileCount += child.FileCount;
		}
	}

	private static void Compress(Node node) {
		for (int i = 0; i < node.Children.Count; i++) {
			Node child = node.Children[i];

			if (child.Kind != RowKind.Folder) continue;

			while (child.Children is [{ Kind: RowKind.Folder } only]) {
				Node merged = new() { Kind = RowKind.Folder, Name = child.Name + "/" + only.Name, Key = only.Key, Collapsed = only.Collapsed, Insertions = only.Insertions, Deletions = only.Deletions, FileCount = only.FileCount };
				merged.Children.AddRange(only.Children);
				child = merged;
			}

			node.Children[i] = child;
			Compress(child);
		}
	}

	private static int CommonPrefix(string[][] paths) {
		if (paths.Length == 0) return 0;

		int length = paths[0].Length - 1;

		foreach (string[] path in paths) {
			int shared = 0;

			while (shared < length && shared < path.Length - 1 && path[shared] == paths[0][shared]) shared++;

			length = shared;
		}

		return length;
	}

	private void Flatten() {
		_rows.Clear();

		void Visit(List<Node> nodes, int depth) {
			foreach (Node node in nodes) {
				node.Depth = depth;
				_rows.Add(node);

				if (!node.Collapsed) Visit(node.Children, depth + 1);
			}
		}

		Visit(_tree, 0);
		ClampScroll();
	}

	private int FirstFileRow() => _rows.FindIndex(static row => row.Kind == RowKind.File);

	private string Relative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

	#endregion

	#region Content

	private void ShowFile(int index, SearchMatch? reveal) {
		if (index < 0 || index >= _changes.Count) return;

		if (index == _selectedFile && _view is not null) {
			Reveal(reveal);
			Repaint();
			return;
		}

		_selectedFile = index;
		_pendingReveal = reveal;

		if (!_texts.TryGetValue(index, out (string Original, string Modified) texts)) {
			if (!_prefetched.TryRemove(index, out texts)) {
				int generation = ++_loadGeneration;

				ThreadPool.UnsafeQueueUserWorkItem(_ => {
					_prefetched[index] = Load(index);

					if (generation != _loadGeneration) return;

					_loaded = index;
					_changed();
				}, null);

				SetThinking(true);
				Repaint();
				return;
			}

			Remember(index, texts);
		}

		Display(index, texts);
	}

	private volatile int _loadGeneration;
	private volatile int _loaded = -1;

	private void Display(int index, (string Original, string Modified) texts) {
		Touch(index);

		TextBuffer buffer = new(texts.Modified);
		TextView view = new(buffer, _changes[index].Path);
		buffer.ReadOnly = true;
		view.Highlighter.SetLanguage(Forge.Syntax.Languages.ForPath(_changes[index].Path));

		_diff?.Detach();
		_view = view;
		_diff = new DiffEditor(view, texts.Original, _changes[index].Path, canStage: false) { HideUnchangedRegions = Forge.Core.Settings.Current.GetBoolean("diffEditor.hideUnchangedRegions.enabled") ?? false };
		Repaint();
	}

	private (string Original, string Modified) Load(int index) {
		GitCommitChange change = _changes[index];
		string relative = Relative(change.Path);
		string originalRelative = change.OriginalPath is { } previous ? Relative(previous) : relative;
		string original = change.Status == GitFileStatus.Added ? string.Empty : GitHistoryReader.Show(_root, _from, originalRelative) ?? string.Empty;
		string modified = change.Status == GitFileStatus.Deleted ? string.Empty : GitHistoryReader.Show(_root, _to, relative) ?? string.Empty;
		return (original, modified);
	}

	private void Remember(int index, (string Original, string Modified) texts) {
		_texts[index] = texts;

		while (_texts.Count > ModelCacheSize && _recent.Last is { } oldest) {
			_recent.RemoveLast();

			if (oldest.Value != _selectedFile) _texts.Remove(oldest.Value);
		}
	}

	private void Touch(int index) {
		_recent.Remove(index);
		_recent.AddFirst(index);
	}

	private void Prefetch() {
		int[] order = [.. _rows.Where(static row => row.Kind == RowKind.File).Select(static row => row.FileIndex)];

		if (order.Length <= 1) order = [.. Enumerable.Range(0, _changes.Count)];

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			int loaded = 0;

			foreach (int index in order) {
				if (loaded >= PrefetchLimit) break;

				if (index == _selectedFile) continue;

				_prefetched.TryAdd(index, Load(index));
				loaded++;
			}
		}, null);
	}

	private void Reveal(SearchMatch? target) {
		if (target is null || _diff is null || _view is null) return;

		TextView view = target.Original ? _diff.Original : _diff.Modified;
		TextSnapshot snapshot = view.Snapshot;
		int line = Math.Clamp(target.Line, 0, Math.Max(0, snapshot.LineCount - 1));
		int start = snapshot.GetLineSpan(line).Start + target.Column;

		_diff.ShowAllRegions();
		view.MoveTo(start, extend: false);
		view.MoveTo(start + target.Length, extend: true);

		if (view.LastViewportHeight > 0f) view.RevealLineInCenter(line, view.LastLineHeight, view.LastViewportHeight);

		_diff.Modified.SetScrollY(view.ScrollY);
	}

	#endregion

	#region Search

	private void QueueSearch() {
		_searchDue = Environment.TickCount64 + SearchDebounce;
		SetThinking(true);
	}

	public override void OnThink(float deltaSeconds) {
		if (_loaded is var loaded and >= 0 && loaded == _selectedFile && _prefetched.TryRemove(loaded, out (string Original, string Modified) texts)) {
			_loaded = -1;
			Remember(loaded, texts);
			Display(loaded, texts);
		}

		if (_pendingMatches is { } matches) {
			_pendingMatches = null;
			ApplyMatches(matches);
		}

		if (_searchDue == 0 || Environment.TickCount64 < _searchDue) {
			if (_searchDue == 0 && _searchStatus is not "Searching…" && _texts.ContainsKey(_selectedFile)) SetThinking(false);

			return;
		}

		_searchDue = 0;
		RunSearch();
	}

	private void RunSearch() {
		string query = _search.GetString();
		int generation = ++_searchGeneration;

		if (query.Length == 0) {
			_matches = null;
			_searchStatus = null;
			BuildTree(null);
			Flatten();
			Repaint();
			SetThinking(false);
			return;
		}

		_searchStatus = "Searching…";
		Repaint();

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			Dictionary<int, List<SearchMatch>> found = Search(query, out bool capped);

			if (generation != _searchGeneration) return;

			_searchCapped = capped;
			_pendingMatches = found;
			_changed();
		}, null);
	}

	private Dictionary<int, List<SearchMatch>> Search(string query, out bool capped) {
		capped = false;
		Dictionary<int, List<SearchMatch>> found = [];
		string? patch = GitProcess.Run(_root, "diff", "-U0", "--no-color", "-M", _from, _to, "--");

		if (patch is null) return found;

		Dictionary<string, int> byPath = new(StringComparer.Ordinal);

		for (int i = 0; i < _changes.Count; i++) byPath[Relative(_changes[i].Path)] = i;

		int file = -1;
		int originalLine = 0;
		int modifiedLine = 0;
		int total = 0;

		foreach (string raw in patch.Split('\n')) {
			string line = raw.TrimEnd('\r');

			if (line.StartsWith("+++ ", StringComparison.Ordinal)) {
				string target = line[4..];

				if (target.StartsWith("b/", StringComparison.Ordinal)) target = target[2..];

				if (target != "/dev/null") file = byPath.GetValueOrDefault(target, -1);

				continue;
			}

			if (line.StartsWith("--- ", StringComparison.Ordinal)) {
				string target = line[4..];

				if (target.StartsWith("a/", StringComparison.Ordinal)) target = target[2..];

				file = target == "/dev/null" ? -1 : byPath.GetValueOrDefault(target, -1);

				if (file < 0) {
					foreach ((string path, int index) in byPath) {
						if (_changes[index].OriginalPath is { } previous && Relative(previous) == target) file = index;
					}
				}

				continue;
			}

			if (line.StartsWith("@@", StringComparison.Ordinal)) {
				ParseHunk(line, out originalLine, out modifiedLine);
				continue;
			}

			if (file < 0 || line.Length == 0) continue;

			bool original = line[0] == '-';

			if (line[0] is not ('+' or '-')) continue;

			string text = line[1..];
			int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);

			while (at >= 0) {
				if (!found.TryGetValue(file, out List<SearchMatch>? list)) found[file] = list = [];

				list.Add(new SearchMatch(file, original, (original ? originalLine : modifiedLine) - 1, at, query.Length, text));

				if (++total >= SearchResultLimit) {
					capped = true;
					return found;
				}

				at = text.IndexOf(query, at + Math.Max(1, query.Length), StringComparison.OrdinalIgnoreCase);
			}

			if (original) originalLine++;
			else modifiedLine++;
		}

		return found;
	}

	private static void ParseHunk(string header, out int original, out int modified) {
		original = 1;
		modified = 1;

		int minus = header.IndexOf('-');
		int plus = header.IndexOf('+');

		if (minus >= 0) original = ReadNumber(header, minus + 1);

		if (plus >= 0) modified = ReadNumber(header, plus + 1);
	}

	private static int ReadNumber(string text, int at) {
		int value = 0;

		while (at < text.Length && char.IsAsciiDigit(text[at])) value = (value * 10) + (text[at++] - '0');

		return value;
	}

	private void ApplyMatches(Dictionary<int, List<SearchMatch>> matches) {
		_matches = matches;
		int results = matches.Values.Sum(static list => list.Count);

		_searchStatus = results == 0 ? "No results" : _searchCapped ? $"{results}+ results in {matches.Count} files" : results == 1 ? "1 result in changed lines" : $"{results} results in {matches.Count} files";
		BuildTree(matches);
		Flatten();
		_focusRow = -1;
		Repaint();
	}

	#endregion

	#region Layout

	private float FilesWidth => Math.Clamp(_filesWidth * Scale, MinimumFilesWidth * Scale, Math.Max(MinimumFilesWidth * Scale, Math.Min(MaximumFilesWidth * Scale, Wide - (MinimumContentWidth * Scale))));

	private float TreeTop => (HeaderHeight + SearchHeight) * Scale;

	private float ContentLeft => FilesWidth;

	private float DiffTop => HeaderHeight * Scale;

	public override void PerformLayout() {
		float scale = Scale;
		_search.SetBounds(10f * scale, HeaderHeight * scale, Math.Max(0f, FilesWidth - (20f * scale)), 26f * scale);
		base.PerformLayout();
	}

	private void ClampScroll() {
		float content = _rows.Count * RowHeight * Scale;
		_scroll = Math.Clamp(_scroll, 0f, Math.Max(0f, content - (Tall - TreeTop)));
	}

	#endregion

	#region Input

	private int RowAt(float x, float y) {
		if (x >= FilesWidth || y < TreeTop) return -1;

		int row = (int)((y - TreeTop + _scroll) / (RowHeight * Scale));
		return row >= 0 && row < _rows.Count ? row : -1;
	}

	private bool OnFilesSash(float x) => Math.Abs(x - FilesWidth) <= SashSize * Scale / 2f;

	public override void OnCursorMoved(float x, float y) {
		if (_sashDragging) {
			_filesWidth = Math.Clamp(x / Scale, MinimumFilesWidth, MaximumFilesWidth);
			InvalidateLayout();
			Repaint();
			return;
		}

		(float absX, float absY) = GetAbsPos();

		if (_diffSashDragging && _diff is not null) {
			_diff.DragSash(absX + x);
			Repaint();
			return;
		}

		if (_selecting && _diff is not null) {
			TextView target = _diff.Focused;
			target.MoveTo(OffsetAt(target, absX + x, absY + y), extend: true);
			Repaint();
			return;
		}

		bool sash = OnFilesSash(x);

		if (sash != _sashHover) {
			_sashHover = sash;
			Repaint();
		}

		int row = RowAt(x, y);

		if (row != _hoverRow) {
			_hoverRow = row;
			Repaint();
		}

		if (sash) {
			Cursor = CursorCode.SizeWestEast;
			return;
		}

		if (_diff is not null && x > ContentLeft && y > DiffTop) {
			if (_diff.Hover(absX + x, absY + y)) Repaint();

			Cursor = _diff.Cursor ?? (_diff.IsInModified(absX + x) || _diff.IsInOriginal(absX + x) ? CursorCode.IBeam : CursorCode.Arrow);
			return;
		}

		Cursor = CursorCode.Arrow;
	}

	public override void OnCursorExited() {
		_hoverRow = -1;
		_sashHover = false;
		Repaint();
	}

	private int OffsetAt(TextView view, float absX, float absY) {
		(float paneX, float paneY) = GetAbsPos();
		float left = paneX + ContentLeft + (ReferenceEquals(view, _diff!.Original) ? 0f : _diff.ModifiedLeft);
		return view.OffsetFromPoint(Ui!.GetFonts().GetCode(), absX, absY, left, paneY + DiffTop);
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) => Press(button, x, y, 1);

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) => Press(button, x, y, 2);

	public override bool OnMouseTriplePressed(MouseButton button, float x, float y) => Press(button, x, y, 3);

	private bool Press(MouseButton button, float x, float y, int clicks) {
		RequestFocus();

		if (button != MouseButton.Left) return true;

		if (OnFilesSash(x)) {
			_sashDragging = true;
			Ui?.SetMouseCapture(this);
			return true;
		}

		if (x < FilesWidth) {
			int row = RowAt(x, y);

			if (row < 0) return true;

			Activate(row, toggle: true);
			return true;
		}

		if (_diff is null || y < DiffTop) {
			if (_diff is not null && y < DiffTop && GoToFileHit(x, y)) _openFile(_changes[_selectedFile].Path);

			return true;
		}

		(float absX, float absY) = GetAbsPos();
		float px = absX + x;
		float py = absY + y;

		if (_diff.IsOnSash(px, py)) {
			_diffSashDragging = true;
			_diff.SashDragging = true;
			Ui?.SetMouseCapture(this);
			Repaint();
			return true;
		}

		if (_diff.Press(Ui!.GetFonts().GetCode(), px, py, clicks, Tall - DiffTop)) {
			Repaint();
			return true;
		}

		bool original = _diff.IsInOriginal(px);
		_diff.Focus(original);
		TextView view = _diff.Focused;
		int offset = OffsetAt(view, px, py);

		if (clicks >= 3) view.SelectLineAt(offset);
		else if (clicks == 2) view.SelectWordAt(offset);
		else view.MoveTo(offset, extend: (Ui.GetModifiers() & KeyModifiers.Shift) != 0);

		_selecting = true;
		Ui.SetMouseCapture(this);
		Repaint();
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		StopDragging();
		return true;
	}

	public override void OnMouseCaptureLost() => StopDragging();

	private void StopDragging() {
		_sashDragging = false;
		_selecting = false;

		if (_diffSashDragging && _diff is not null) _diff.SashDragging = false;

		_diffSashDragging = false;

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		Repaint();
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		if (x < FilesWidth) {
			_scroll -= delta / 120f * 3f * RowHeight * Scale;
			ClampScroll();
			Repaint();
			return true;
		}

		if (_view is null) return true;

		_view.ScrollByWheel(delta, _view.LastLineHeight, Tall - DiffTop);
		Repaint();
		return true;
	}

	private void Activate(int row, bool toggle) {
		Node node = _rows[row];
		_focusRow = row;

		switch (node.Kind) {
			case RowKind.Folder:
				if (toggle) {
					node.Collapsed = !node.Collapsed;
					Flatten();
				}

				break;

			case RowKind.File:
				if (toggle && node.Children.Count > 0) {
					node.Collapsed = !node.Collapsed;
					Flatten();
				}

				ShowFile(node.FileIndex, null);
				break;

			case RowKind.Match:
				ShowFile(node.FileIndex, node.Match);
				break;
		}

		RevealRow(_focusRow);
		Repaint();
	}

	private void RevealRow(int row) {
		if (row < 0) return;

		float height = RowHeight * Scale;
		float viewport = Tall - TreeTop;

		if (row * height < _scroll) _scroll = row * height;
		else if (((row + 1) * height) > _scroll + viewport) _scroll = ((row + 1) * height) - viewport;

		ClampScroll();
	}

	public void NextFile(int direction) {
		int[] files = [.. Enumerable.Range(0, _rows.Count).Where(i => _rows[i].Kind == RowKind.File)];

		if (files.Length == 0) return;

		int current = Array.FindIndex(files, i => _rows[i].FileIndex == _selectedFile);
		int next = current < 0 ? 0 : ((current + direction) % files.Length + files.Length) % files.Length;
		Activate(files[next], toggle: false);
	}

	public void FocusSearch() {
		_search.RequestFocus();
		_search.SelectAll();
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		bool alt = (modifiers & KeyModifiers.Alt) != 0;
		bool shift = (modifiers & KeyModifiers.Shift) != 0;
		bool control = (modifiers & KeyModifiers.Control) != 0;

		if (key == VirtualKey.F5 && alt) {
			NextFile(shift ? -1 : 1);
			return true;
		}

		if (key == VirtualKey.F && control) {
			FocusSearch();
			return true;
		}

		if (key == VirtualKey.C && control && _diff is not null && _diff.Focused.HasSelection) {
			Clipboard.SetText(_diff.Focused.GetSelectedText());
			return true;
		}

		if (_search.HasFocus) return false;

		switch (key) {
			case VirtualKey.Up when _focusRow > 0:
				Activate(_focusRow - 1, toggle: false);
				return true;

			case VirtualKey.Down when _focusRow < _rows.Count - 1:
				Activate(_focusRow + 1, toggle: false);
				return true;

			case VirtualKey.Left when _focusRow >= 0 && _rows[_focusRow] is { Collapsed: false, Children.Count: > 0 } open:
				open.Collapsed = true;
				Flatten();
				Repaint();
				return true;

			case VirtualKey.Right when _focusRow >= 0 && _rows[_focusRow] is { Collapsed: true } closed:
				closed.Collapsed = false;
				Flatten();
				Repaint();
				return true;

			case VirtualKey.Return or VirtualKey.Space when _focusRow >= 0:
				Activate(_focusRow, toggle: true);
				return true;
		}

		return base.OnKeyCodePressed(key, modifiers);
	}

	#endregion

	#region Painting

	private (float Left, float Top, float Size) GoToFileBounds() {
		float scale = Scale;
		float size = 22f * scale;
		return (Wide - (12f * scale) - size, ((HeaderHeight * scale) - size) / 2f, size);
	}

	private bool GoToFileHit(float x, float y) {
		if (_selectedFile < 0) return false;

		(float left, float top, float size) = GoToFileBounds();
		return x >= left && x < left + size && y >= top && y < top + size;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float files = FilesWidth;

		painter.FillRectangle(ScreenX, ScreenY, files, Tall, Theme.SidebarBackground);
		painter.FillRectangle(ScreenX + files, ScreenY, Wide - files, Tall, Theme.EditorBackground);

		PaintFilesHeader(painter, scale, files);
		PaintTree(painter, scale, files);
		PaintContent(painter, scale, files);

		painter.DrawVerticalLine(ScreenX + files - 1f, ScreenY, ScreenY + Tall, Theme.PanelBorder);

		if (_sashHover || _sashDragging) painter.FillRectangle(ScreenX + files - (SashSize * scale / 2f), ScreenY, SashSize * scale, Tall, Theme.FocusBorder);
	}

	private void PaintFilesHeader(IPainter painter, float scale, float width) {
		ITextFont font = painter.GetUi(11f, strong: true);
		float height = HeaderHeight * scale;
		float baseline = ScreenY + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float left = ScreenX + (12f * scale);
		float right = ScreenX + width - (12f * scale);

		if (_searchStatus is { } status) {
			painter.DrawText(font, status.ToUpperInvariant(), left, baseline, Theme.DescriptionForeground);
			return;
		}

		string label = (_changes.Count == 1 ? "1 file changed" : $"{_changes.Count} files changed").ToUpperInvariant();
		painter.PushClip(left, ScreenY, Math.Max(0f, right - left), height);
		painter.DrawText(font, label, left, baseline, Theme.DescriptionForeground);
		painter.PopClip();

		int insertions = _changes.Sum(static change => change.Insertions);
		int deletions = _changes.Sum(static change => change.Deletions);
		PaintStats(painter, insertions, deletions, right, ScreenY + (height / 2f), scale);
	}

	private static float PaintStats(IPainter painter, int insertions, int deletions, float right, float centreY, float scale) {
		ITextFont small = painter.GetUi(11f);
		float smallBaseline = centreY - (small.GetLineHeight() / 2f) + small.GetBaseline();

		if (deletions > 0) {
			string text = "−" + deletions;
			right -= small.Measure(text);
			painter.DrawText(small, text, right, smallBaseline, Theme.GitDeletedForeground.WithAlpha(0.9f));
			right -= 6f * scale;
		}

		if (insertions > 0) {
			string text = "+" + insertions;
			right -= small.Measure(text);
			painter.DrawText(small, text, right, smallBaseline, Theme.GitAddedForeground.WithAlpha(0.9f));
			right -= 6f * scale;
		}

		return right;
	}

	private void PaintTree(IPainter painter, float scale, float width) {
		float top = ScreenY + TreeTop;
		float height = RowHeight * scale;

		painter.PushClip(ScreenX, top, width, Math.Max(0f, Tall - TreeTop));

		int first = Math.Max(0, (int)(_scroll / height));

		for (int i = first; i < _rows.Count; i++) {
			float rowTop = top + (i * height) - _scroll;

			if (rowTop > ScreenY + Tall) break;

			PaintRow(painter, _rows[i], i, rowTop, height, width, scale);
		}

		painter.PopClip();
	}

	private void PaintRow(IPainter painter, Node node, int index, float top, float height, float width, float scale) {
		if (index == _focusRow) painter.FillRectangle(ScreenX, top, width, height, HasFocus ? Theme.ListActiveSelectionBackground : Theme.ListInactiveSelectionBackground);
		else if (index == _hoverRow) painter.FillRectangle(ScreenX, top, width, height, Theme.ListHoverBackground);

		float iconSize = Metrics.IconSize * scale;
		float indent = ScreenX + (8f * scale) + (node.Depth * 8f * scale);
		float centreY = top + (height / 2f);
		bool expandable = node.Kind == RowKind.Folder || (node.Kind == RowKind.File && node.Children.Count > 0);

		if (expandable) painter.DrawIcon(node.Collapsed ? Codicon.ChevronRight : Codicon.ChevronDown, indent + (iconSize / 2f), centreY, iconSize, Theme.Foreground);

		float left = indent + iconSize + (6f * scale);
		float right = ScreenX + width - (10f * scale);
		ITextFont font = painter.GetUi();
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();

		switch (node.Kind) {
			case RowKind.Folder: {
					if (index != _focusRow) painter.FillRoundedRectangle(left - (2f * scale), top + scale, right - left + (4f * scale), height - (2f * scale), 3f * scale, Theme.SectionHeaderBackground);

					painter.DrawIcon(Codicon.Folder, left + (8f * scale), centreY, 14f * scale, Theme.IconForeground.WithAlpha(0.9f));
					left += 16f * scale + (6f * scale);

					float statsLeft = PaintStats(painter, node.Insertions, node.Deletions, right, centreY, scale);
					painter.PushClip(left, top, Math.Max(0f, statsLeft - left), height);
					painter.DrawText(painter.GetUiStrong(), node.Name, left, baseline, Theme.Foreground);
					painter.PopClip();
					break;
				}

			case RowKind.File: {
					GitCommitChange change = _changes[node.FileIndex];
					(Codicon icon, Color color) = StatusIcon(change.Status);
					painter.DrawIcon(icon, left + (8f * scale), centreY, 14f * scale, color);
					left += 16f * scale + (6f * scale);

					painter.DrawFileIcon(node.Name, left + (iconSize / 2f), centreY, iconSize);
					left += iconSize + (4f * scale);

					float statsLeft = right;

					if (_matches is not null && node.Children.Count > 0) {
						ITextFont small = painter.GetUi(10f);
						string count = node.Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
						float badge = Math.Max(16f * scale, small.Measure(count) + (8f * scale));
						statsLeft -= badge;
						painter.FillRoundedRectangle(statsLeft, centreY - (8f * scale), badge, 16f * scale, 8f * scale, Theme.BadgeBackground);
						painter.DrawText(small, count, statsLeft + ((badge - small.Measure(count)) / 2f), centreY - (small.GetLineHeight() / 2f) + small.GetBaseline(), Theme.BadgeForeground);
						statsLeft -= 6f * scale;
					}
					else statsLeft = PaintStats(painter, node.Insertions, node.Deletions, right, centreY, scale);

					painter.PushClip(left, top, Math.Max(0f, statsLeft - left), height);
					painter.DrawText(font, node.Name, left, baseline, change.Status == GitFileStatus.Deleted ? Theme.DescriptionForeground : Theme.Foreground);

					if (change.Status == GitFileStatus.Deleted) painter.DrawHorizontalLine(MathF.Round(centreY), left, left + font.Measure(node.Name), Theme.DescriptionForeground);

					painter.PopClip();
					break;
				}

			case RowKind.Match when node.Match is { } match: {
					ITextFont code = painter.GetCode();
					string number = (match.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
					float codeBaseline = top + ((height - code.GetLineHeight()) / 2f) + code.GetBaseline();

					painter.PushClip(left, top, Math.Max(0f, right - left), height);
					painter.DrawText(code, number, left, codeBaseline, Theme.Foreground);
					left += code.Measure(number) + (6f * scale);

					ReadOnlySpan<char> text = match.Text.AsSpan().TrimStart();
					int trimmed = match.Text.Length - text.Length;
					int start = Math.Max(0, match.Column - trimmed);
					painter.DrawText(code, text[..Math.Min(start, text.Length)], left, codeBaseline, Theme.DescriptionForeground);
					float highlightLeft = left + code.Measure(text[..Math.Min(start, text.Length)]);
					ReadOnlySpan<char> hit = text[Math.Min(start, text.Length)..Math.Min(start + match.Length, text.Length)];
					painter.DrawText(code, hit, highlightLeft, codeBaseline, Theme.ListHighlightForeground);
					painter.DrawText(code, text[Math.Min(start + match.Length, text.Length)..], highlightLeft + code.Measure(hit), codeBaseline, Theme.DescriptionForeground);
					painter.PopClip();
					break;
				}
		}
	}

	private static (Codicon Icon, Color Color) StatusIcon(GitFileStatus status) => status switch {
		GitFileStatus.Added => (Codicon.DiffAdded, Theme.GitAddedForeground),
		GitFileStatus.Deleted => (Codicon.DiffRemoved, Theme.GitDeletedForeground),
		_ => (Codicon.DiffModified, Theme.GitModifiedForeground),
	};

	private void PaintContent(IPainter painter, float scale, float files) {
		float left = ScreenX + files;
		float width = Wide - files;
		float header = HeaderHeight * scale;

		if (_selectedFile < 0) {
			ITextFont font = painter.GetUi();
			painter.DrawText(font, "This commit does not change any files.", left + (20f * scale), ScreenY + (20f * scale) + font.GetBaseline(), Theme.DescriptionForeground);
			return;
		}

		GitCommitChange change = _changes[_selectedFile];
		ITextFont ui = painter.GetUi(12f);
		float centreY = ScreenY + (header / 2f);
		float baseline = centreY - (ui.GetLineHeight() / 2f) + ui.GetBaseline();
		float x = left + (12f * scale);
		(Codicon icon, Color color) = StatusIcon(change.Status);

		painter.DrawIcon(icon, x + (8f * scale), centreY, 14f * scale, color);
		x += 16f * scale + (8f * scale);

		(float goLeft, float goTop, float goSize) = GoToFileBounds();
		float statsRight = PaintStats(painter, change.Insertions, change.Deletions, ScreenX + goLeft - (8f * scale), centreY, scale);

		painter.PushClip(x, ScreenY, Math.Max(0f, statsRight - x - (8f * scale)), header);
		string path = Relative(change.Path);
		float pathWidth = ui.Measure(path);
		float available = statsRight - x - (8f * scale);
		painter.DrawText(ui, path, pathWidth > available ? statsRight - (8f * scale) - pathWidth : x, baseline, Theme.DescriptionForeground);
		painter.PopClip();

		painter.DrawIcon(Codicon.GoToFile, ScreenX + goLeft + (goSize / 2f), ScreenY + goTop + (goSize / 2f), 16f * scale, Theme.IconForeground);
		painter.DrawHorizontalLine(ScreenY + header - 1f, left, left + width, Theme.PanelBorder);

		if (_diff is null || _view is null) return;

		float diffTop = ScreenY + header;
		float diffHeight = Math.Max(0f, Tall - header);

		painter.PushClip(left, diffTop, width, diffHeight);
		_view.Scale = scale;
		_diff.Original.Scale = scale;
		_diff.Render(painter, left, diffTop, width, diffHeight, scale);
		painter.PopClip();

		if (_pendingReveal is { } reveal && _view.LastViewportHeight > 0f) {
			_pendingReveal = null;
			Reveal(reveal);
			Repaint();
		}
	}

	#endregion
}
