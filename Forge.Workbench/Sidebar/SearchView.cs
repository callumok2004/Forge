using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Search;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class SearchView : Panel
{
	private const float ContainerLeft = 2f;
	private const float ContainerRight = 12f;
	private const float ContainerPadding = 6f;
	private const float InputLeft = 18f;
	private const float InputHeight = 26f;
	private const float PatternHeight = 25f;
	private const float RowGap = 6f;
	private const float ToggleTop = 3f;
	private const float ToggleRight = 2f;
	private const float ToggleGap = 2f;
	private const float ReplaceToggleWidth = 16f;
	private const float MoreWidth = 25f;
	private const float MoreHeight = 16f;
	private const float DetailsMinHeight = 13f;
	private const float HeadingHeight = 19f;
	private const float MessagePaddingX = 22f;
	private const float MessagePaddingBottom = 8f;
	private const float MessageLineHeight = 18f;
	private const float RowHeight = 22f;
	private const float Indent = 8f;
	private const float Twistie = 16f;
	private const float TwistiePadding = 6f;
	private const float IconGap = 6f;
	private const float BadgeMargin = 12f;
	private const float ActionSize = 20f;
	private const float ActionsPaddingRight = 10f;
	private const float ActionsPaddingLeft = 5f;
	private const int DebounceMilliseconds = 300;
	private const int BeforeChars = 26;
	private const int MaxPreviewChars = 250;

	private readonly TextEntry _find;
	private readonly TextEntry _replace;
	private readonly TextEntry _include;
	private readonly TextEntry _exclude;
	private readonly InputToggle _matchCase;
	private readonly InputToggle _wholeWord;
	private readonly InputToggle _regex;
	private readonly InputToggle _preserveCase;
	private readonly InputToggle _openEditors;
	private readonly InputToggle _useExclude;
	private readonly ActionButton _replaceAll;
	private readonly ActionButton _viewMode;

	private readonly List<FileSearchResult> _results = [];
	private readonly ConcurrentQueue<FileSearchResult> _incoming = new();
	private readonly HashSet<string> _collapsed = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly List<Row> _rows = [];

	private CancellationTokenSource? _cancellation;
	private Regex? _activeRegex;
	private long _changedAt;
	private bool _pending;
	private volatile bool _searching;
	private bool _rowsDirty = true;
	private bool _replaceVisible;
	private bool _detailsVisible;
	private string? _error;
	private int _matchCount;
	private float _scrollY;
	private int _hover = -1;
	private int _hoverAction = -1;
	private int _selected = -1;
	private bool _hoverReplaceToggle;
	private bool _hoverMore;

	private enum RowKind : byte
	{
		Folder,
		File,
		Match,
	}

	private readonly record struct Row(RowKind Kind, int File, int Match, int Depth, string? Key, string? Label, int Count, int FileStart, int FileEnd)
	{
		public bool IsFile => Kind == RowKind.File;

		public bool IsMatch => Kind == RowKind.Match;
	}

	private sealed class FolderNode(string name, string path)
	{
		public readonly string Name = name;
		public readonly string Path = path;
		public readonly List<FolderNode> Folders = [];
		public readonly List<int> Files = [];
		public int Count;
		public int First = int.MaxValue;
		public int Last = -1;
	}

	public SearchView() {
		Name = "Search";
		Dock = DockType.Fill;
		MouseInputEnabled = true;

		_find = new TextEntry { Name = "SearchInput", Placeholder = "Search", FontSize = Metrics.FontSizeBody, TextChanged = QueueSearch, Submitted = SearchNow, FocusLost = SearchIfPending };
		_replace = new TextEntry { Name = "ReplaceInput", Placeholder = "Replace", FontSize = Metrics.FontSizeBody, Visible = false, TextChanged = ReplaceChanged };
		_include = new TextEntry { Name = "IncludeInput", Placeholder = "e.g. *.ts, src/**/include", FontSize = Metrics.FontSizeBody, Visible = false, TextChanged = QueueSearch, Submitted = SearchNow, FocusLost = SearchIfPending };
		_exclude = new TextEntry { Name = "ExcludeInput", Placeholder = "e.g. *.ts, src/**/exclude", FontSize = Metrics.FontSizeBody, Visible = false, TextChanged = QueueSearch, Submitted = SearchNow, FocusLost = SearchIfPending };

		_matchCase = Toggle(_find, Codicon.CaseSensitive, "Match Case");
		_wholeWord = Toggle(_find, Codicon.WholeWord, "Match Whole Word");
		_regex = Toggle(_find, Codicon.Regex, "Use Regular Expression");
		_preserveCase = Toggle(_replace, Codicon.PreserveCase, "Preserve Case");
		_preserveCase.Changed = _ => Repaint();
		_openEditors = Toggle(_include, Codicon.Book, "Search only in Open Editors");
		_useExclude = Toggle(_exclude, Codicon.Exclude, "Use Exclude Settings and Ignore Files");
		_useExclude.Checked = true;

		_find.TextInset = new Thickness(0f, 0f, ToggleStripWidth(3), 0f);
		_replace.TextInset = new Thickness(0f, 0f, ToggleStripWidth(1), 0f);
		_include.TextInset = new Thickness(0f, 0f, ToggleStripWidth(1), 0f);
		_exclude.TextInset = new Thickness(0f, 0f, ToggleStripWidth(1), 0f);

		_replaceAll = new ActionButton { Name = "ReplaceAll", Icon = Codicon.ReplaceAll, Foreground = Theme.Foreground, Radius = Metrics.RadiusSmall, Visible = false, Action = ReplaceAllResults };

		AddChild(_find);
		AddChild(_replace);
		AddChild(_replaceAll);
		AddChild(_include);
		AddChild(_exclude);

		HeaderActions = [
			new ActionButton { Name = "RefreshSearch", Icon = Codicon.Refresh, Foreground = Theme.Foreground, Radius = Metrics.RadiusSmall, Action = SearchNow },
			new ActionButton { Name = "ClearSearch", Icon = Codicon.ClearAll, Foreground = Theme.Foreground, Radius = Metrics.RadiusSmall, Action = Clear },
			_viewMode = new ActionButton { Name = "SearchViewMode", Icon = Codicon.ListFlat, Foreground = Theme.Foreground, Radius = Metrics.RadiusSmall, Action = ToggleViewMode },
			new ActionButton { Name = "CollapseSearch", Icon = Codicon.CollapseAll, Foreground = Theme.Foreground, Radius = Metrics.RadiusSmall, Action = ToggleCollapseAll },
		];
	}

	public IReadOnlyList<ActionButton> HeaderActions { get; }

	public string RootPath { get; set; } = string.Empty;

	public Func<IReadOnlyList<string>>? OpenEditorPaths { get; set; }

	public Action<string, int, int, int, bool>? MatchOpened { get; set; }

	public Action<string, IReadOnlyList<(TextMatch Match, string Replacement)>>? ReplaceRequested { get; set; }

	public TextEntry Input => _find;

	public bool TreeView { get; private set; } = true;

	public Func<IReadOnlyList<string>>? ExcludeSettings { get; set; }

	public Action<string, IReadOnlyList<(TextMatch Match, string Replacement)>, int>? ReplacePreviewRequested { get; set; }

	private void ToggleViewMode() {
		TreeView = !TreeView;
		_viewMode.Icon = TreeView ? Codicon.ListFlat : Codicon.ListTree;
		_rowsDirty = true;
		_selected = -1;
		Repaint();
	}

	public void FocusFolder(string relative) {
		_detailsVisible = true;
		_include.SetText("./" + relative.Replace('\\', '/'));
		InvalidateLayout();
		Focus(null);
		QueueSearch();
	}

	public void Focus(string? seed) {
		if (seed is { Length: > 0 }) _find.SetText(seed);

		_find.RequestFocus();
		_find.SelectAll();
	}

	private InputToggle Toggle(TextEntry host, Codicon icon, string title) {
		InputToggle toggle = new(icon, title) { Changed = _ => SearchNow() };
		host.AddChild(toggle);
		return toggle;
	}

	private static float ToggleStripWidth(int count) => (count * (InputToggle.Size + ToggleGap)) + ToggleRight;

	#region Searching

	private void QueueSearch() {
		_changedAt = Environment.TickCount64;
		_pending = true;
		SetThinking(true);
	}

	private void ReplaceChanged() {
		_replaceAll.Enabled = _replace.Text.Length > 0 || _results.Count > 0;
		Repaint();
	}

	private void SearchIfPending() {
		if (_pending) SearchNow();
	}

	public void SearchNow() {
		_pending = false;
		_cancellation?.Cancel();
		_cancellation = null;
		_incoming.Clear();
		_results.Clear();
		_matchCount = 0;
		_rowsDirty = true;
		_selected = -1;
		_scrollY = 0f;

		TextSearchQuery query = new(_find.GetString(), _matchCase.Checked, _wholeWord.Checked, _regex.Checked, IncludePattern(), _exclude.GetString(), _useExclude.Checked) { SettingsExcludes = ExcludeSettings?.Invoke() };
		_activeRegex = TextSearchEngine.Compile(query, out _error);

		if (_activeRegex is not { } regex || RootPath.Length == 0) {
			_searching = false;
			Repaint();
			return;
		}

		CancellationTokenSource cancellation = new();
		_cancellation = cancellation;
		_searching = true;
		string root = RootPath;

		Task.Run(() => {
			TextSearchEngine.Run(root, query, regex, _incoming.Enqueue, cancellation.Token);

			if (!cancellation.IsCancellationRequested) _searching = false;
		});

		SetThinking(true);
		Repaint();
	}

	private string IncludePattern() {
		string include = _include.GetString();

		if (!_openEditors.Checked || OpenEditorPaths?.Invoke() is not { } open) return include;

		List<string> paths = [];

		foreach (string path in open) {
			if (path.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase)) paths.Add("./" + Path.GetRelativePath(RootPath, path).Replace('\\', '/'));
		}

		return paths.Count == 0 ? "./\u0000" : string.Join(',', paths);
	}

	public void Clear() {
		_cancellation?.Cancel();
		_cancellation = null;
		_searching = false;
		_find.Clear();
		_replace.Clear();
		_results.Clear();
		_incoming.Clear();
		_matchCount = 0;
		_rowsDirty = true;
		_error = null;
		Repaint();
	}

	public override void OnThink(float deltaSeconds) {
		if (_pending && Environment.TickCount64 - _changedAt >= DebounceMilliseconds) SearchNow();

		bool added = false;

		while (_incoming.TryDequeue(out FileSearchResult? result)) {
			int index = _results.BinarySearch(result, ResultOrder.Instance);
			_results.Insert(index < 0 ? ~index : index, result);
			_matchCount += result.Matches.Length;
			added = true;
		}

		if (added) {
			_rowsDirty = true;
			_replaceAll.Enabled = true;
			Repaint();
		}

		if (!_pending && !_searching && _incoming.IsEmpty) {
			SetThinking(false);
			Repaint();
		}
	}

	private sealed class ResultOrder : IComparer<FileSearchResult>
	{
		public static readonly ResultOrder Instance = new();

		public int Compare(FileSearchResult? x, FileSearchResult? y) => string.Compare(x?.RelativePath, y?.RelativePath, StringComparison.OrdinalIgnoreCase);
	}

	#endregion

	#region Replace

	private string ReplacementFor(FileSearchResult file, int index) {
		string pattern = _replace.GetString();
		TextMatch match = file.Matches[index];
		string line = file.Lines[index];
		string matched = line.Substring(match.Column, Math.Min(match.Length, line.Length - match.Column));
		string replacement = pattern;

		if (_regex.Checked && _activeRegex is { } regex) {
			Match found = regex.Match(line, match.Column);
			replacement = found.Success && found.Index == match.Column ? found.Result(pattern) : pattern;
		}

		if (!_preserveCase.Checked || matched.Length == 0 || replacement.Length == 0) return replacement;

		if (matched.ToUpperInvariant() == matched) return replacement.ToUpperInvariant();

		if (matched.ToLowerInvariant() == matched) return replacement.ToLowerInvariant();

		if (char.IsUpper(matched[0])) return char.ToUpperInvariant(replacement[0]) + replacement[1..];

		return replacement;
	}

	private void ReplaceFile(int fileIndex) {
		FileSearchResult file = _results[fileIndex];
		List<(TextMatch, string)> edits = new(file.Matches.Length);

		for (int i = 0; i < file.Matches.Length; i++) edits.Add((file.Matches[i], ReplacementFor(file, i)));

		ReplaceRequested?.Invoke(file.Path, edits);
		_results.RemoveAt(fileIndex);
		_matchCount -= file.Matches.Length;
		_rowsDirty = true;
		Repaint();
	}

	private void ReplaceMatch(int fileIndex, int matchIndex) {
		FileSearchResult file = _results[fileIndex];
		ReplaceRequested?.Invoke(file.Path, [(file.Matches[matchIndex], ReplacementFor(file, matchIndex))]);
		DismissMatch(fileIndex, matchIndex);
	}

	private void ReplaceAllResults() {
		if (_results.Count == 0) return;

		int files = _results.Count;
		int matches = _matchCount;
		string replacement = _replace.GetString();
		string message = replacement.Length > 0
			? $"Replace {matches} occurrence{(matches == 1 ? "" : "s")} across {files} file{(files == 1 ? "" : "s")} with '{replacement}'?"
			: $"Replace {matches} occurrence{(matches == 1 ? "" : "s")} across {files} file{(files == 1 ? "" : "s")}?";

		ConfirmDialog.Show(message, string.Empty, "Replace", () => {
			for (int i = _results.Count - 1; i >= 0; i--) ReplaceFile(i);
		});
	}

	private void DismissFile(int fileIndex) {
		_matchCount -= _results[fileIndex].Matches.Length;
		_results.RemoveAt(fileIndex);
		_rowsDirty = true;
		Repaint();
	}

	private void DismissMatch(int fileIndex, int matchIndex) {
		FileSearchResult file = _results[fileIndex];

		if (file.Matches.Length == 1) {
			DismissFile(fileIndex);
			return;
		}

		TextMatch[] matches = [.. file.Matches[..matchIndex], .. file.Matches[(matchIndex + 1)..]];
		string[] lines = [.. file.Lines[..matchIndex], .. file.Lines[(matchIndex + 1)..]];
		_results[fileIndex] = new FileSearchResult(file.Path, file.RelativePath, lines, matches);
		_matchCount--;
		_rowsDirty = true;
		Repaint();
	}

	#endregion

	#region Rows

	private void EnsureRows() {
		if (!_rowsDirty) return;

		_rowsDirty = false;
		_rows.Clear();

		if (TreeView) BuildTree();
		else {
			for (int f = 0; f < _results.Count; f++) AddFile(f, 0);
		}

		if (_selected >= _rows.Count) _selected = -1;
	}

	private void AddFile(int file, int depth) {
		FileSearchResult result = _results[file];
		_rows.Add(new Row(RowKind.File, file, -1, depth, result.Path, Path.GetFileName(result.RelativePath), result.Matches.Length, file, file + 1));

		if (_collapsed.Contains(result.Path)) return;

		for (int m = 0; m < result.Matches.Length; m++) _rows.Add(new Row(RowKind.Match, file, m, depth + 1, null, null, 0, file, file + 1));
	}

	private void BuildTree() {
		FolderNode top = new(string.Empty, string.Empty);

		for (int f = 0; f < _results.Count; f++) {
			string relative = _results[f].RelativePath.Replace('\\', '/');
			FolderNode node = top;
			int start = 0;

			Track(node, f);

			while (true) {
				int slash = relative.IndexOf('/', start);

				if (slash < 0) break;

				string name = relative[start..slash];
				FolderNode? child = node.Folders.Count > 0 && string.Equals(node.Folders[^1].Name, name, StringComparison.OrdinalIgnoreCase) ? node.Folders[^1] : null;

				if (child is null) {
					child = new FolderNode(name, relative[..slash]);
					node.Folders.Add(child);
				}

				node = child;
				Track(node, f);
				start = slash + 1;
			}

			node.Files.Add(f);
		}

		EmitChildren(top, 0);
	}

	private void Track(FolderNode node, int file) {
		node.Count += _results[file].Matches.Length;
		node.First = Math.Min(node.First, file);
		node.Last = Math.Max(node.Last, file);
	}

	private void EmitChildren(FolderNode node, int depth) {
		int folder = 0;
		int file = 0;

		while (folder < node.Folders.Count || file < node.Files.Count) {
			bool takeFolder = file >= node.Files.Count || (folder < node.Folders.Count && node.Folders[folder].First < node.Files[file]);

			if (takeFolder) EmitFolder(node.Folders[folder++], depth);
			else AddFile(node.Files[file++], depth);
		}
	}

	private void EmitFolder(FolderNode node, int depth) {
		string label = node.Name;

		while (node.Files.Count == 0 && node.Folders.Count == 1) {
			node = node.Folders[0];
			label += "/" + node.Name;
		}

		string key = "dir:" + node.Path;
		_rows.Add(new Row(RowKind.Folder, node.First, -1, depth, key, label, node.Count, node.First, node.Last + 1));

		if (!_collapsed.Contains(key)) EmitChildren(node, depth + 1);
	}

	private void ToggleCollapseAll() {
		if (_collapsed.Count > 0) _collapsed.Clear();
		else {
			foreach (Row row in _rows) {
				if (row.Key is { } key && row.Depth == 0) _collapsed.Add(key);
			}
		}

		_rowsDirty = true;
		Repaint();
	}

	#endregion

	#region Layout

	private float HeaderBottom { get; set; }

	private float ResultsTop => HeaderBottom + (HasMessage ? (((MessageLineHeight * MessageLineCount()) + MessagePaddingBottom - 5f) * Scale) : 0f);

	private string MessageText {
		get {
			if (_error is not null) return _error;

			if (_results.Count == 0) return _searching ? string.Empty : "No results found. Review your settings for configured exclusions and check your gitignore files.";

			int files = _results.Count;
			return $"{_matchCount} result{(_matchCount == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}";
		}
	}

	private float MessageWidth => Math.Max(0f, Wide - (MessagePaddingX * 2f * Scale));

	private int MessageLineCount() {
		if (Ui is null) return 1;

		ITextFont font = Ui.GetFonts().GetUi();
		ReadOnlySpan<char> text = MessageText;
		int count = 0;

		while (text.Length > 0) {
			text = text[WrapLength(font, text, MessageWidth)..].TrimStart(' ');
			count++;
		}

		return Math.Max(1, count);
	}

	private static int WrapLength(ITextFont font, ReadOnlySpan<char> text, float width) {
		if (font.Measure(text) <= width) return text.Length;

		int fit = 1;

		while (fit < text.Length && font.Measure(text[..(fit + 1)]) <= width) fit++;

		int space = text[..Math.Min(text.Length, fit + 1)].LastIndexOf(' ');
		return space > 0 ? space : fit;
	}

	public override void PerformLayout() {
		float scale = Scale;
		float left = (ContainerLeft + InputLeft) * scale;
		float width = Math.Max(0f, Wide - left - (ContainerRight * scale));
		float y = ContainerPadding * scale;

		_find.SetBounds(left, y, width, InputHeight * scale);
		LayoutToggles(_find, [_matchCase, _wholeWord, _regex], scale);
		y += InputHeight * scale;

		_replace.Visible = _replaceVisible;
		_replaceAll.Visible = _replaceVisible;

		if (_replaceVisible) {
			y += RowGap * scale;
			float action = ActionSize * scale;
			float replaceWidth = Math.Max(0f, width - action - (4f * scale));
			_replace.SetBounds(left, y, replaceWidth, InputHeight * scale);
			LayoutToggles(_replace, [_preserveCase], scale);
			_replaceAll.SetBounds(left + replaceWidth + (4f * scale), y + ((InputHeight - ActionSize) / 2f * scale), action, action);
			y += InputHeight * scale;
		}

		_include.Visible = _detailsVisible;
		_exclude.Visible = _detailsVisible;
		_detailsTop = y;

		if (_detailsVisible) {
			y += HeadingHeight * scale;
			_include.SetBounds(left, y, width, PatternHeight * scale);
			LayoutToggles(_include, [_openEditors], scale);
			y += (PatternHeight + HeadingHeight) * scale;
			_exclude.SetBounds(left, y, width, PatternHeight * scale);
			LayoutToggles(_exclude, [_useExclude], scale);
			y += (PatternHeight + 4f) * scale;
		}
		else {
			y += DetailsMinHeight * scale;
		}

		y += ContainerPadding * scale;
		HeaderBottom = y;


		base.PerformLayout();
	}

	private float _detailsTop;

	private static void LayoutToggles(TextEntry host, InputToggle[] toggles, float scale) {
		float size = InputToggle.Size * scale;
		float right = host.Wide - (ToggleRight * scale);
		float top = Math.Max(0f, (host.Tall - size) / 2f);

		for (int i = toggles.Length - 1; i >= 0; i--) {
			toggles[i].SetBounds(right - size, top, size, size);
			right -= size + (ToggleGap * scale);
		}
	}

	private bool HasMessage => _error is not null || _results.Count > 0 || (!_searching && _activeRegex is not null && _find.Text.Length > 0);

	#endregion

	#region Input

	private (float Left, float Top, float Size) ReplaceToggleBounds {
		get {
			float scale = Scale;
			float height = (_replaceVisible ? (InputHeight * 2f) + RowGap : InputHeight) * scale;
			return (ContainerLeft * scale, ContainerPadding * scale, height);
		}
	}

	private (float Left, float Top) MoreBounds => (Wide - ((ContainerRight - 2f + MoreWidth) * Scale), _detailsVisible ? _detailsTop : _detailsTop);

	private int RowAt(float y) {
		float top = ResultsTop - _scrollY;

		if (y < ResultsTop || y < top) return -1;

		int row = (int)((y - top) / (RowHeight * Scale));
		return row < _rows.Count ? row : -1;
	}

	private int ActionCount(in Row row) => _replaceVisible ? 2 : 1;

	private int ActionAt(int row, float x) {
		if (row < 0) return -1;

		float scale = Scale;
		float right = Wide - (ActionsPaddingRight * scale);
		int count = ActionCount(_rows[row]);

		for (int i = count - 1; i >= 0; i--) {
			float left = right - (ActionSize * scale);

			if (x >= left && x < right) return i;

			right = left;
		}

		return -1;
	}

	private const float ScrollbarWidth = 10f;
	private const float MinimumThumb = 20f;

	private bool _draggingThumb;
	private float _dragOffset;
	private bool _hoverThumb;

	private float Viewport => Math.Max(0f, Tall - ResultsTop);

	private float ContentHeight => _rows.Count * RowHeight * Scale;

	private float MaximumScroll => Math.Max(0f, ContentHeight - Viewport);

	private bool HasScrollbar => ContentHeight > Viewport && Viewport > 0f;

	private (float Top, float Height) Thumb {
		get {
			float viewport = Viewport;
			float height = Math.Max(MinimumThumb * Scale, viewport * viewport / ContentHeight);
			float top = MaximumScroll <= 0f ? 0f : _scrollY / MaximumScroll * (viewport - height);
			return (ResultsTop + top, height);
		}
	}

	private bool InScrollbar(float x, float y) => HasScrollbar && x >= Wide - (ScrollbarWidth * Scale) && y >= ResultsTop;

	private void ScrollToThumb(float y) {
		(_, float height) = Thumb;
		float track = Viewport - height;

		if (track <= 0f) return;

		_scrollY = Math.Clamp((y - _dragOffset - ResultsTop) / track * MaximumScroll, 0f, MaximumScroll);
		Repaint();
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_draggingThumb) return true;

		_draggingThumb = false;

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		Repaint();
		return true;
	}

	public override void OnMouseCaptureLost() {
		_draggingThumb = false;
		Repaint();
	}

	public override void OnCursorMoved(float x, float y) {
		if (_draggingThumb) {
			ScrollToThumb(y);
			return;
		}

		bool overThumb = InScrollbar(x, y);

		if (overThumb != _hoverThumb) {
			_hoverThumb = overThumb;
			Repaint();
		}

		if (!_scrollbarShown && HasScrollbar) Repaint();

		_scrollbarShown = HasScrollbar;

		if (overThumb) {
			Cursor = CursorCode.Arrow;
			return;
		}
		EnsureRows();

		(float toggleLeft, float toggleTop, float toggleHeight) = ReplaceToggleBounds;
		bool overToggle = x >= toggleLeft && x < toggleLeft + (ReplaceToggleWidth * Scale) && y >= toggleTop && y < toggleTop + toggleHeight;
		(float moreLeft, float moreTop) = MoreBounds;
		bool overMore = x >= moreLeft && x < moreLeft + (MoreWidth * Scale) && y >= moreTop && y < moreTop + (MoreHeight * Scale);
		int row = RowAt(y);
		int action = ActionAt(row, x);

		Cursor = overToggle || overMore || action >= 0 ? CursorCode.Hand : CursorCode.Arrow;

		if (overToggle == _hoverReplaceToggle && overMore == _hoverMore && row == _hover && action == _hoverAction) return;

		_hoverReplaceToggle = overToggle;
		_hoverMore = overMore;
		_hover = row;
		_hoverAction = action;
		Repaint();
	}

	private bool _scrollbarShown;

	public override void OnCursorExited() {
		_scrollbarShown = false;
		_hoverThumb = false;
		_hover = -1;
		_hoverAction = -1;
		_hoverReplaceToggle = false;
		_hoverMore = false;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return true;

		EnsureRows();

		if (InScrollbar(x, y)) {
			(float top, float height) = Thumb;

			if (y < top || y >= top + height) {
				_scrollY = Math.Clamp(_scrollY + ((y < top ? -1f : 1f) * Viewport), 0f, MaximumScroll);
				(top, _) = Thumb;
			}

			_draggingThumb = true;
			_dragOffset = Math.Clamp(y - top, 0f, height);
			Ui?.SetMouseCapture(this);
			Repaint();
			return true;
		}

		if (_hoverReplaceToggle) {
			_replaceVisible = !_replaceVisible;
			InvalidateLayout();
			Repaint();
			return true;
		}

		if (_hoverMore) {
			_detailsVisible = !_detailsVisible;
			InvalidateLayout();
			Repaint();
			return true;
		}

		int index = RowAt(y);

		if (index < 0) return true;

		Row row = _rows[index];
		int action = ActionAt(index, x);

		if (action >= 0) {
			bool replace = _replaceVisible && action == 0;

			if (row.Kind == RowKind.Folder) {
				for (int f = row.FileEnd - 1; f >= row.FileStart; f--) {
					if (replace) ReplaceFile(f);
					else DismissFile(f);
				}
			}
			else if (row.IsFile) {
				if (replace) ReplaceFile(row.File);
				else DismissFile(row.File);
			}
			else if (replace) {
				ReplaceMatch(row.File, row.Match);
			}
			else {
				DismissMatch(row.File, row.Match);
			}

			return true;
		}

		_selected = index;
		RequestFocus();

		if (!row.IsMatch) {
			string key = row.Key!;

			if (!_collapsed.Remove(key)) _collapsed.Add(key);

			_rowsDirty = true;
		}
		else {
			OpenRow(row, preserveFocus: true);
		}

		Repaint();
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		EnsureRows();

		int index = RowAt(y);

		if (index >= 0 && _rows[index].IsMatch) OpenRow(_rows[index], preserveFocus: false);

		return true;
	}

	private void OpenRow(Row row, bool preserveFocus) {
		if ((uint)row.File >= (uint)_results.Count || (uint)row.Match >= (uint)_results[row.File].Matches.Length) return;

		FileSearchResult file = _results[row.File];
		TextMatch match = file.Matches[row.Match];

		if (_replaceVisible && _replace.Text.Length > 0 && ReplacePreviewRequested is { } preview) {
			List<(TextMatch, string)> edits = new(file.Matches.Length);

			for (int i = 0; i < file.Matches.Length; i++) edits.Add((file.Matches[i], ReplacementFor(file, i)));

			preview(file.Path, edits, match.Line);
			return;
		}

		MatchOpened?.Invoke(file.Path, match.Line, match.Column, match.Length, preserveFocus);
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		EnsureRows();

		if (_rows.Count == 0) return false;

		switch (key) {
			case VirtualKey.Down:
				_selected = Math.Min(_rows.Count - 1, _selected + 1);
				break;
			case VirtualKey.Up:
				_selected = Math.Max(0, _selected - 1);
				break;
			case VirtualKey.Return when _selected >= 0 && _rows[_selected].IsMatch:
				OpenRow(_rows[_selected], preserveFocus: false);
				return true;
			default:
				return false;
		}

		if (_rows[_selected].IsMatch) OpenRow(_rows[_selected], preserveFocus: true);

		RevealSelected();
		Repaint();
		return true;
	}

	private void RevealSelected() {
		float height = RowHeight * Scale;
		float viewport = Tall - ResultsTop;
		float top = _selected * height;

		if (top < _scrollY) _scrollY = top;
		else if (top + height > _scrollY + viewport) _scrollY = top + height - viewport;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		EnsureRows();

		float height = RowHeight * Scale;
		float maximum = Math.Max(0f, (_rows.Count * height) - (Tall - ResultsTop));
		float previous = _scrollY;
		_scrollY = Math.Clamp(_scrollY - (delta / 120f * 3f * height), 0f, maximum);

		if (_scrollY != previous) Repaint();

		return true;
	}

	#endregion

	#region Painting

	public override void Paint(IPainter painter) {
		EnsureRows();

		float scale = Scale;
		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		PaintReplaceToggle(painter, scale);
		PaintDetails(painter, scale);
		PaintMessage(painter, scale);
		PaintRows(painter, scale);
		PaintScrollbar(painter, scale);

		painter.PopClip();
	}

	private void PaintReplaceToggle(IPainter painter, float scale) {
		(float left, float top, float height) = ReplaceToggleBounds;
		float width = ReplaceToggleWidth * scale;

		if (_hoverReplaceToggle) painter.FillRoundedRectangle(ScreenX + left, ScreenY + top, width, height, Metrics.RadiusSmall * scale, Theme.ToolbarHoverBackground);

		painter.DrawIcon(_replaceVisible ? Codicon.ChevronDown : Codicon.ChevronRight, ScreenX + left + (width / 2f), ScreenY + top + (height / 2f), Metrics.IconSize * scale, Theme.Foreground);
	}

	private void PaintDetails(IPainter painter, float scale) {
		(float moreLeft, float moreTop) = MoreBounds;
		float width = MoreWidth * scale;
		float height = MoreHeight * scale;

		if (_hoverMore) painter.FillRoundedRectangle(ScreenX + moreLeft, ScreenY + moreTop, width, height, Metrics.RadiusSmall * scale, Theme.ToolbarHoverBackground);

		painter.DrawIcon(Codicon.Ellipsis, ScreenX + moreLeft + (width / 2f), ScreenY + moreTop + (height / 2f), Metrics.IconSize * scale, Theme.Foreground);

		if (!_detailsVisible) return;

		ITextFont font = painter.GetUi(11f);
		float left = ScreenX + ((ContainerLeft + InputLeft) * scale);
		float includeHeading = ScreenY + _detailsTop + (4f * scale);
		float excludeHeading = ScreenY + _exclude.Y - (HeadingHeight * scale) + (4f * scale);

		painter.DrawText(font, "files to include", left, includeHeading + font.GetBaseline(), Theme.Foreground);
		painter.DrawText(font, "files to exclude", left, excludeHeading + font.GetBaseline(), Theme.Foreground);
	}

	private void PaintMessage(IPainter painter, float scale) {
		if (!HasMessage) return;

		ReadOnlySpan<char> text = MessageText;

		if (text.Length == 0) return;

		ITextFont font = painter.GetUi();
		float width = MessageWidth;
		float line = MessageLineHeight * scale;
		float left = ScreenX + (MessagePaddingX * scale);
		float top = ScreenY + HeaderBottom - (5f * scale);
		Color color = _error is not null ? Theme.ErrorForeground : Theme.SearchResultsInfoForeground;
		painter.PushClip(left, top, width, line * MessageLineCount());

		while (text.Length > 0) {
			int length = WrapLength(font, text, width);
			painter.DrawText(font, text[..length], left, top + ((line - font.GetLineHeight()) / 2f) + font.GetBaseline(), color);
			text = text[length..].TrimStart(' ');
			top += line;
		}

		painter.PopClip();
	}

	private void PaintScrollbar(IPainter painter, float scale) {
		if (!HasScrollbar || !(IsCursorOver || _draggingThumb)) return;

		(float top, float height) = Thumb;
		float width = ScrollbarWidth * scale;
		Color color = _draggingThumb ? Theme.ScrollbarSliderActive : _hoverThumb ? Theme.ScrollbarSliderHover : Theme.ScrollbarSlider;
		painter.FillRectangle(ScreenX + Wide - width, ScreenY + top, width, height, color);
	}

	private void PaintRows(IPainter painter, float scale) {
		float height = RowHeight * scale;
		float areaTop = ScreenY + ResultsTop;
		float top = areaTop - _scrollY;
		int first = Math.Max(0, (int)(_scrollY / height));

		painter.PushClip(ScreenX, areaTop, Wide, Math.Max(0f, Tall - ResultsTop));

		for (int i = first; i < _rows.Count; i++) {
			float rowTop = top + (i * height);

			if (rowTop >= ScreenY + Tall) break;

			if (!painter.NeedsPaint(ScreenX, rowTop, Wide, height)) continue;

			PaintRow(painter, i, rowTop, height, scale);
		}

		painter.PopClip();
	}

	private void PaintRow(IPainter painter, int index, float top, float height, float scale) {
		Row row = _rows[index];
		FileSearchResult file = _results[row.File];
		bool hovered = index == _hover;
		bool selected = index == _selected;
		float centreY = top + (height / 2f);
		float iconSize = Metrics.IconSize * scale;
		ITextFont font = painter.GetUi();
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();

		if (selected) painter.FillRectangle(ScreenX, top, Wide, height, HasFocus ? Theme.ListActiveSelectionBackground : Theme.ListInactiveSelectionBackground);
		else if (hovered) painter.FillRectangle(ScreenX, top, Wide, height, Theme.ListHoverBackground);

		bool showActions = hovered || selected;
		int actionCount = ActionCount(row);
		float actionsRight = ScreenX + Wide - (ActionsPaddingRight * scale);
		float actionsLeft = actionsRight - (actionCount * ActionSize * scale) - (ActionsPaddingLeft * scale);
		Color foreground = selected && HasFocus ? Theme.ListActiveSelectionForeground : Theme.Foreground;

		float indent = ScreenX + (row.Depth * Indent * scale);

		for (int level = 1; level <= row.Depth; level++) painter.FillRectangle(ScreenX + ((((level - 1) * Indent) + (Twistie / 2f)) * scale), top, Metrics.StrokeThickness * scale, height, Theme.TreeIndentGuide);

		if (!row.IsMatch) {
			bool collapsed = _collapsed.Contains(row.Key!);
			painter.DrawIcon(collapsed ? Codicon.ChevronRight : Codicon.ChevronDown, indent + (Twistie * scale / 2f), centreY, iconSize, Theme.Foreground);

			float content = indent + ((Twistie + TwistiePadding) * scale);

			if (row.Kind == RowKind.Folder) {
				if (!painter.DrawFolderIcon(row.Label!.AsSpan(row.Label!.LastIndexOf('/') + 1), !collapsed, content + (iconSize / 2f), centreY, iconSize)) painter.DrawIcon(Codicon.Folder, content + (iconSize / 2f), centreY, iconSize, Theme.Foreground);
			}
			else {
				painter.DrawFileIcon(row.Label!, content + (iconSize / 2f), centreY, iconSize);
			}

			float labelLeft = content + iconSize + (IconGap * scale);
			float badgeWidth = Badge.Width(painter.GetUiSmall(), row.Count, scale);
			float labelRight = showActions ? actionsLeft : ScreenX + Wide - ((BadgeMargin * scale) + badgeWidth);
			string? directory = row.IsFile && !TreeView ? Path.GetDirectoryName(file.RelativePath)?.Replace('/', Path.DirectorySeparatorChar) : null;

			painter.PushClip(labelLeft, top, Math.Max(0f, labelRight - labelLeft), height);
			ITextFont strong = painter.GetUi(Metrics.FontSizeBody, strong: true);
			painter.DrawText(strong, row.Label!, labelLeft, baseline, foreground);

			if (directory is { Length: > 0 }) painter.DrawText(font, directory, labelLeft + strong.Measure(row.Label!) + (Metrics.Space6 * scale), baseline, Theme.DescriptionForeground);

			painter.PopClip();

			if (!showActions) Badge.Draw(painter, row.Count, ScreenX + Wide - (BadgeMargin * scale) - badgeWidth, centreY, scale, Theme.BadgeBackground, Theme.BadgeForeground);
		}
		else {
			float content = indent + ((Twistie + TwistiePadding) * scale);
			float right = showActions ? actionsLeft : ScreenX + Wide - (ActionsPaddingRight * scale);
			painter.PushClip(content, top, Math.Max(0f, right - content), height);
			PaintPreview(painter, font, file, row.Match, content, top, height, baseline, foreground, selected && HasFocus ? foreground : null);
			painter.PopClip();
		}

		if (!showActions) return;

		float actionRight = actionsRight;

		for (int i = actionCount - 1; i >= 0; i--) {
			float left = actionRight - (ActionSize * scale);
			bool hot = hovered && i == _hoverAction;
			Codicon icon = _replaceVisible && i == 0 ? row.IsMatch ? Codicon.Replace : Codicon.ReplaceAll : Codicon.Close;

			if (hot) painter.FillRoundedRectangle(left, centreY - (ActionSize * scale / 2f), ActionSize * scale, ActionSize * scale, Metrics.RadiusSmall * scale, Theme.ToolbarHoverBackground);

			painter.DrawIcon(icon, left + (ActionSize * scale / 2f), centreY, iconSize, Theme.Foreground);
			actionRight = left;
		}
	}

	private void PaintPreview(IPainter painter, ITextFont font, FileSearchResult file, int index, float left, float top, float height, float baseline, Color foreground, Color? selectedForeground) {
		string line = file.Lines[index];
		TextMatch match = file.Matches[index];
		int start = Math.Min(match.Column, line.Length);
		int end = Math.Min(start + match.Length, line.Length);

		ReadOnlySpan<char> before = Cut(line.AsSpan(0, start), out bool cut);
		ReadOnlySpan<char> inside = line.AsSpan(start, end - start);
		int remaining = MaxPreviewChars - before.Length - (cut ? 1 : 0);
		inside = inside[..Math.Min(inside.Length, Math.Max(0, remaining))];
		remaining -= inside.Length;
		ReadOnlySpan<char> after = line.AsSpan(end);
		after = after[..Math.Min(after.Length, Math.Max(0, remaining))];

		float x = left;
		Color context = selectedForeground ?? Theme.DescriptionForeground;

		if (cut) {
			painter.DrawText(font, "…", x, baseline, context);
			x += font.Measure("…");
		}

		painter.DrawText(font, before, x, baseline, context);
		x += font.Measure(before);

		float insideWidth = font.Measure(inside);
		string replacement = _replaceVisible && _replace.Text.Length > 0 ? ReplacementFor(file, index) : string.Empty;

		if (replacement.Length > 0) {
			painter.FillRectangle(x, top + (2f * Scale), insideWidth, height - (4f * Scale), Theme.DiffRemovedTextBackground);
			painter.DrawText(font, inside, x, baseline, foreground);
			painter.DrawHorizontalLine(MathF.Round(top + (height / 2f)), x, x + insideWidth, foreground);
			x += insideWidth;

			float replacementWidth = font.Measure(replacement);
			painter.FillRectangle(x, top + (2f * Scale), replacementWidth, height - (4f * Scale), Theme.DiffInsertedTextBackground);
			painter.DrawText(font, replacement, x, baseline, foreground);
			x += replacementWidth;
		}
		else {
			painter.FillRectangle(x, top + (2f * Scale), insideWidth, height - (4f * Scale), Theme.FindMatchHighlightBackground);
			painter.DrawText(font, inside, x, baseline, foreground);
			x += insideWidth;
		}

		painter.DrawText(font, after, x, baseline, context);
	}

	private static ReadOnlySpan<char> Cut(ReadOnlySpan<char> text, out bool cut) {
		ReadOnlySpan<char> trimmed = text.TrimStart();
		cut = false;

		if (trimmed.Length < BeforeChars) return trimmed;

		int boundary = 0;

		for (int i = 1; i < trimmed.Length; i++) {
			if (IsWord(trimmed[i - 1]) == IsWord(trimmed[i])) continue;

			if (trimmed.Length - i < BeforeChars) break;

			boundary = i;
		}

		if (boundary == 0) return trimmed;

		cut = true;
		return trimmed[boundary..].TrimStart();
	}

	private static bool IsWord(char character) => char.IsLetterOrDigit(character) || character == '_';

	#endregion
}
