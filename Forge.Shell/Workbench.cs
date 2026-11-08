using Forge.VSCode;
using Forge.Commands;
using Forge.Common.Commands;
using Forge.Common.Debug;
using Forge.Common.Editor;
using Forge.Common.Extensions;
using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core;
using Forge.Editor;
using Forge.Extensions.Discord;
using Forge.Extensions.TrailingSpaces;
using Forge.Extensions;
using Forge.Git;
using Forge.Language;
using Forge.Render;
using Forge.Search;
using Forge.Text;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.UI;
using Forge.Workspace;

namespace Forge.Shell;

public sealed class Workbench(float scale) : IEditorHost, IDisposable
{
	private readonly List<Document> _documents = [];
	private readonly List<TextView> _views = [];
	private readonly List<EditorPane?> _panes = [];
	private readonly Minimap _minimap = new();

	private const int LargeDocumentChars = 4 * 1024 * 1024;

	private float _scale = scale;

	public float Scale {
		get => _scale;
		set => _scale = value;
	}

	public TitleBar TitleBar { get; } = new();

	public ActivityBar ActivityBar { get; } = new();

	public Sidebar Sidebar { get; } = new();

	public TabStrip Tabs { get; } = new();

	public Breadcrumbs Breadcrumbs { get; } = new();

	public StatusBar StatusBar { get; } = new();

	public QuickInput QuickInput { get; } = new();

	public QuickOpen QuickOpen => field ??= new QuickOpen(OpenFile);

	public CommandPalette Palette => field ??= new CommandPalette(Registry, static command => Common.UI.Panel.Ui?.PostCommand(command.GetId(), null));

	public FindWidget Find { get; } = new();

	public PanelPart Panel { get; } = new();

	public AuxiliaryBar Auxiliary { get; } = new();

	public Common.Webview.IWebviewHost Webviews { get; set; } = Common.Webview.NullWebviewHost.Instance;

	public IntelliSense? IntelliSense { get; set; }

	public string? ActiveFilePath => ActiveDocument?.Path;

	public string? GetActiveFilePath() => ActiveFilePath;

	public IDocument? GetActiveDocument() => ActiveDocument;

	public ITextView? GetActiveView() => ActiveView;

	public IReadOnlyList<Document> Documents => _documents;

	public int ActiveIndex { get; private set; } = -1;

	public Document? ActiveDocument => ActiveIndex >= 0 && _panes[ActiveIndex] is null ? _documents[ActiveIndex] : null;

	public TextView? ActiveView => ActiveIndex >= 0 && _panes[ActiveIndex] is null ? _views[ActiveIndex] : null;

	public EditorPane? ActivePane => ActiveIndex >= 0 ? _panes[ActiveIndex] : null;

	public Codicon? PaneIcon(int index) => _panes[index]?.Icon;

	public EditorPane? FindPaneAt(int index) => (uint)index < (uint)_panes.Count ? _panes[index] : null;

	public EditorPane? FindPane(string id) {
		foreach (EditorPane? pane in _panes) {
			if (pane is not null && pane.Id == id) return pane;
		}

		return null;
	}

	public void OpenPane(EditorPane pane) {
		for (int i = 0; i < _panes.Count; i++) {
			if (_panes[i] is { } existing && existing.Id == pane.Id) {
				Activate(i);
				return;
			}
		}

		_documents.Add(Document.Untitled(pane.Title, string.Empty));
		_views.Add(new TextView(_documents[^1].Buffer));
		_panes.Add(pane);
		ActiveIndex = _documents.Count - 1;
		SyncBreadcrumbs();
	}

	public void ClosePane(string id) {
		for (int i = 0; i < _panes.Count; i++) {
			if (_panes[i]?.Id == id) {
				Close(i);
				return;
			}
		}
	}

	public bool ShowSidebar { get; set; } = true;

	public bool PanelFocused {
		get => Panel.HasFocusWithin;
		set {
			if (value) Panel.FocusActive();
			else FocusEditor?.Invoke();
		}
	}

	public Action? FocusEditor { get; set; }

	public ProcessExplorer? ProcessExplorer { get; set; }

	public bool ShowMinimap { get; set; } = true;

	public string? RootPath { get; set; }

	public string? BranchName { get; set; }

	public IGitRepository? Repository { get; set; }

	public IExtensionRegistry? Extensions { get; set; }

	public IExtensionHost? ExtensionHost { get; set; }

	public WorkbenchViews? Views { get; set; }

	public VSCodeBridge? VSCode { get; set; }

	public ExtensionHostSync? ExtensionSync { get; set; }

	public void SyncEditorActions() {
		if (VSCode is not { } bridge) return;

		List<(string Command, string Title, Codicon Icon)> actions = [];

		foreach (VSCodeBridge.EditorAction action in bridge.EditorTitleActions) {
			actions.Add((action.Command, action.Title, action.Icon is { } icon && Forge.UI.Controls.Label.TryParseIcon(icon, out Codicon parsed) ? parsed : Codicon.Run));
		}

		Tabs.SetActions(actions);
	}

	public Action<string>? CommandInvoked;

	public bool RunCommand(string id, ICommandHost host) {
		CommandInvoked?.Invoke(id);
		return Registry.Execute(id, host);
	}


	public Func<int, BreakpointMark>? BreakpointMarkFor { get; set; }

	public Func<string, int>? ExecutionLineFor { get; set; }


	public EditorDecorations Decorations { get; } = new();

	public TrailingSpacesExtension? TrailingSpaces { get; set; }

	public DiscordPresenceExtension? Presence { get; set; }

	private Action<DocumentEvent, string, string?>[] _documentObservers = [];

	private readonly Lock _observerSync = new();

	public void ObserveDocuments(Action<DocumentEvent, string, string?> observer) {
		lock (_observerSync) _documentObservers = [.. _documentObservers, observer];
	}

	private readonly Dictionary<string, int> _publishedVersions = [with(StringComparer.OrdinalIgnoreCase)];

	private void PublishDocument(DocumentEvent kind, string path, string? text) {
		foreach (Action<DocumentEvent, string, string?> observer in _documentObservers.AsSpan()) {
			observer(kind, path, text);
		}
	}

	public void PublishActiveDocument() {
		if (ActiveDocument is { } document) Publish(document);
	}

	public void Publish(Document document) {
		if (_documentObservers.Length == 0 || document.Path is not { } path) return;

		int version = document.Buffer.Version;

		if (_publishedVersions.TryGetValue(path, out int published)) {
			if (published == version) return;

			_publishedVersions[path] = version;
			PublishDocument(DocumentEvent.Changed, path, document.Buffer.GetText());
			return;
		}

		_publishedVersions[path] = version;
		PublishDocument(DocumentEvent.Opened, path, document.Buffer.GetText());
	}

	public GitDiff Diff { get; } = new();

	public void RefreshDiff(Action refreshed) {
		if (Repository is not { } repository || ActiveDocument?.Path is not { } path) {
			Diff.Clear();
			return;
		}

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			Diff.Refresh(repository, path);
			refreshed();
		}, null);
	}

	public void RefreshGitDiffs(Action refreshed) {
		if (Repository is not { } repository) return;

		string root = repository.GetRootPath();
		List<(Document Document, DiffEditor Diff, string OriginalSpec, string? ModifiedSpec)> targets = [];

		for (int i = 0; i < _documents.Count; i++) {
			if (_panes[i] is not null || !_diffEditors.TryGetValue(_views[i], out DiffEditor? diff) || _documents[i].DiffOriginalUri is not { } original || GitSpec(original) is not { } originalSpec) continue;

			string? modifiedSpec = _documents[i].VirtualUri is { } uri && GitSpec(uri) is { } spec ? spec : null;
			targets.Add((_documents[i], diff, originalSpec, modifiedSpec));
		}

		if (targets.Count == 0) return;

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			List<(Document, DiffEditor, string, string?)> texts = [];

			foreach ((Document document, DiffEditor diff, string originalSpec, string? modifiedSpec) in targets) texts.Add((document, diff, GitText(root, originalSpec), modifiedSpec is null ? null : GitText(root, modifiedSpec)));

			Post(() => {
				foreach ((Document document, DiffEditor diff, string original, string? modified) in texts) {
					if (!_documents.Contains(document)) continue;

					if (!string.Equals(diff.Original.Buffer.GetText(), original, StringComparison.Ordinal)) diff.ReplaceOriginal(original);

					if (modified is not null && !string.Equals(document.Buffer.GetText(), modified, StringComparison.Ordinal)) {
						bool readOnly = document.Buffer.ReadOnly;
						document.Buffer.ReadOnly = false;
						document.Buffer.ReplaceChanged(document.Buffer.GetText(), modified);
						document.Buffer.ReadOnly = readOnly;
						document.MarkSaved();
					}
				}

				refreshed();
			});
		}, null);
	}

	private static string? GitSpec(string uri) {
		if (!uri.StartsWith("git:/", StringComparison.Ordinal)) return null;

		int query = uri.LastIndexOf('?');

		if (query < 0) return null;

		string relative = uri[5..query];
		string reference = uri[(query + 1)..];

		return reference switch {
			"" or "~" => ":" + relative,
			"HEAD" => "HEAD:" + relative,
			_ => null,
		};
	}

	public Action? PaintBackdrop { get; init; }

	public bool ShowFrameStats { get => FrameStatsOverlay.Visible; set => FrameStatsOverlay.Visible = value; }

	public FrameStatsOverlay FrameStatsOverlay { get; } = new();

	public NotificationToasts Notifications { get; } = new();

	public Action<Action> Post { get; set; } = static action => action();

	public Action? RevealCaret { get; set; }

	public DebugStopPresenter? DebugStops { get; set; }

	public PeekPresenter? Peek { get; set; }

	public float PeekRatio { get; set; } = PeekReferencesWidget.DefaultRatio;

	public int PeekHeightInLines { get; set; } = PeekReferencesWidget.DefaultHeightInLines;

	public NavigationHistory History => field ??= new NavigationHistory(this);

	public IFrameStats? FrameStats { get => FrameStatsOverlay.Stats; set => FrameStatsOverlay.Stats = value; }

	#region Layout

	public float ActivityBarWidth => ActivityBar.Width(_scale);

	public float SidebarWidth => ShowSidebar ? Sidebar.Width * _scale : 0f;

	private float ContentLeft => ActivityBarWidth + SidebarWidth;

	public float EditorLeft => ContentLeft + (Metrics.CardMargin * _scale);

	private float EditorRightGutter => Metrics.CardMargin * 2f * _scale;

	public float AuxiliaryWidth => Auxiliary is { IsOpen: true, HasViews: true } ? Auxiliary.Width * _scale : 0f;

	public float AuxiliaryStripWidth => Auxiliary.HasViews ? AuxiliaryStrip.Width(_scale) : 0f;

	public float AuxiliaryStripLeft(float windowWidth) => windowWidth - AuxiliaryStripWidth;

	public float AuxiliaryLeft(float windowWidth) => windowWidth - AuxiliaryStripWidth - AuxiliaryWidth - (Metrics.CardMargin * _scale);

	public float PanelHeight => Panel.IsOpen ? (Panel.Height * _scale) + (Metrics.CardInnerMargin * _scale) : 0f;

	public float PanelTop(float windowHeight) =>
		StatusBarTop(windowHeight) - (Metrics.CardInnerMargin * _scale) - (Panel.Height * _scale);

	public float EditorBottom(float windowHeight) =>
		StatusBarTop(windowHeight) - (Metrics.CardInnerMargin * _scale) - PanelHeight;

	public float ShellTop => TitleBar.Height(_scale);

	public float TabsTop => ShellTop;

	public float BreadcrumbsTop => ShellTop + TabStrip.Height(_scale);

	public float EditorTop => ShellTop + TabStrip.Height(_scale) + Breadcrumbs.Height(_scale);

	public float EditorWidth(float windowWidth) =>
		Math.Max(0f, windowWidth - EditorLeft - EditorRightGutter - AuxiliaryWidth - AuxiliaryStripWidth);

	public float TextWidth(float windowWidth) =>
		Math.Max(0f, EditorWidth(windowWidth) - (ShowMinimap ? Minimap.Width(_scale) : 0f));

	public float EditorHeight(float windowHeight) =>
		Math.Max(0f, EditorBottom(windowHeight) - EditorTop);

	public float StatusBarTop(float windowHeight) => windowHeight - StatusBar.Height(_scale);

	public void AddEditorDamage(DamageRegion damage, IPainter painter, float width, float height) {
		if (ActiveView is not { } view) {
			damage.Add(PixelRect.Around(0f, 0f, width, height));
			return;
		}

		bool floating = FloatingWidgetOpen || _floatingLastFrame;

		damage.Add(floating
			? PixelRect.Around(EditorLeft, EditorTop, EditorWidth(width), EditorHeight(height))
			: view.PendingDamage(painter.GetCode(), EditorLeft, EditorTop, TextWidth(width),
				EditorHeight(height)));

		damage.Add(PixelRect.Around(EditorLeft, ShellTop, EditorWidth(width), EditorTop - ShellTop));

		if (ShowMinimap) {
			damage.Add(PixelRect.Around(EditorLeft + TextWidth(width), EditorTop,
				Minimap.Width(_scale), EditorHeight(height)));
		}

		damage.Add(PixelRect.Around(0f, StatusBarTop(height), width, height - StatusBarTop(height)));
	}

	public PixelRect TitleBarRect(float windowWidth) =>
		PixelRect.Around(0f, 0f, windowWidth, ShellTop);

	public PixelRect ActivityBarRect(float windowHeight) =>
		PixelRect.Around(0f, ShellTop, ActivityBarWidth, StatusBarTop(windowHeight) - ShellTop);

	public PixelRect SidebarRect(float windowHeight) =>
		PixelRect.Around(ActivityBarWidth, ShellTop, EditorLeft - ActivityBarWidth,
			StatusBarTop(windowHeight) - ShellTop);

	public PixelRect TabStripRect(float windowWidth) =>
		PixelRect.Around(EditorLeft, ShellTop, EditorWidth(windowWidth), BreadcrumbsTop - ShellTop);

	public PixelRect EditorCardRect(float windowWidth, float windowHeight) =>
		PixelRect.Around(EditorLeft, ShellTop, EditorWidth(windowWidth), EditorBottom(windowHeight) - ShellTop);

	public PixelRect StatusBarRect(float windowWidth, float windowHeight) =>
		PixelRect.Around(0f, StatusBarTop(windowHeight), windowWidth,
			windowHeight - StatusBarTop(windowHeight));

	public PixelRect PanelRect(float windowWidth, float windowHeight) {
		if (!Panel.IsOpen) return PixelRect.Around(0f, 0f, windowWidth, windowHeight);

		float top = PanelTop(windowHeight);

		return PixelRect.Around(EditorLeft, top, EditorWidth(windowWidth),
			StatusBarTop(windowHeight) - top);
	}

	public bool IsOverSplitter(float x, float windowHeight, float y) {
		if (!ShowSidebar || y < ShellTop || y >= StatusBarTop(windowHeight)) return false;

		float edge = ContentLeft;
		float slop = Metrics.SplitterHitWidth * _scale;
		return x >= edge - slop && x <= edge + slop;
	}

	public void ResizeSidebar(float pointerX) => Sidebar.Resize((pointerX - ActivityBarWidth) / _scale);

	public PixelRect AuxiliaryRect(float windowWidth, float windowHeight) =>
		AuxiliaryWidth <= 0f
			? default
			: PixelRect.Around(AuxiliaryLeft(windowWidth) - (Metrics.CardMargin * _scale), ShellTop,
				AuxiliaryWidth + (Metrics.CardMargin * 2f * _scale), StatusBarTop(windowHeight) - ShellTop);

	public bool IsOverAuxiliarySplitter(float x, float y, float windowWidth, float windowHeight) {
		if (AuxiliaryWidth <= 0f || y < ShellTop || y >= StatusBarTop(windowHeight)) return false;

		float edge = AuxiliaryLeft(windowWidth);
		float slop = Metrics.SplitterHitWidth * _scale;

		return x >= edge - slop && x <= edge + slop;
	}

	public void ResizeAuxiliary(float pointerX, float windowWidth) =>
		Auxiliary.Resize((windowWidth - AuxiliaryStripWidth - pointerX - (Metrics.CardMargin * _scale)) / _scale);

	public bool IsOverPanelSplitter(float x, float y, float windowHeight) {
		if (!Panel.IsOpen || x < EditorLeft) return false;

		float edge = PanelTop(windowHeight);
		float slop = Metrics.SplitterHitWidth * _scale;
		return y >= edge - slop && y <= edge + slop;
	}

	public void ResizePanel(float pointerY, float windowHeight) {
		float available = StatusBarTop(windowHeight) - EditorTop - (80f * _scale);
		Panel.Resize((StatusBarTop(windowHeight) - pointerY) / _scale, available / _scale);
	}

	#endregion

	#region Documents

	private readonly Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<int, LineHint>>> _hintOwners = [with(StringComparer.Ordinal)];
	private readonly Dictionary<string, IReadOnlyDictionary<int, LineHint>?> _mergedHints = [with(StringComparer.OrdinalIgnoreCase)];

	public int LineHintsVersion { get; private set; }

	public void SetLineHints(string owner, IReadOnlyDictionary<string, IReadOnlyDictionary<int, LineHint>>? byPath) {
		if (byPath is null || byPath.Count == 0) _hintOwners.Remove(owner);
		else _hintOwners[owner] = byPath;

		_mergedHints.Clear();
		LineHintsVersion++;
	}

	public IReadOnlyDictionary<int, LineHint>? ExtensionLineHints(string path) {
		if (_hintOwners.Count == 0) return null;

		if (_mergedHints.TryGetValue(path, out IReadOnlyDictionary<int, LineHint>? cached)) return cached;

		Dictionary<int, LineHint>? merged = null;
		IReadOnlyDictionary<int, LineHint>? single = null;

		foreach (IReadOnlyDictionary<string, IReadOnlyDictionary<int, LineHint>> owner in _hintOwners.Values) {
			if (!TryGet(owner, path, out IReadOnlyDictionary<int, LineHint>? hints)) continue;

			if (single is null && merged is null) {
				single = hints;
				continue;
			}

			merged ??= new Dictionary<int, LineHint>(single!);

			foreach ((int line, LineHint hint) in hints) merged.TryAdd(line, hint);
		}

		IReadOnlyDictionary<int, LineHint>? result = merged ?? single;
		_mergedHints[path] = result;
		return result;
	}

	private static bool TryGet(IReadOnlyDictionary<string, IReadOnlyDictionary<int, LineHint>> owner, string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyDictionary<int, LineHint>? hints) {
		if (owner.TryGetValue(path, out hints)) return true;

		foreach ((string key, IReadOnlyDictionary<int, LineHint> value) in owner) {
			if (!string.Equals(key, path, StringComparison.OrdinalIgnoreCase)) continue;

			hints = value;
			return true;
		}

		return false;
	}

	public Dictionary<string, EditorViewState> ViewStates { get; set; } = [];

	public void Open(Document document) {
		TextView view = new(document.Buffer, document.Path) { Diff = Diff };

		if (document.Path is null && document.VirtualUri is { } virtualUri) view.Highlighter.SetLanguage(Forge.Syntax.Languages.ForPath(VirtualFileName(virtualUri)));

		if (document.Path is { } path && ViewStates.TryGetValue(path, out EditorViewState? state)) view.RestoreViewState(state.Anchor, state.Caret, state.ScrollTop);

		int at = _documents.Count;

		if (_openingPreview && PreviewEnabled) {
			int replaced = PreviewIndex;

			if (replaced >= 0 && !_documents[replaced].IsDirty()) {
				Close(replaced);
				at = Math.Min(replaced, _documents.Count);
			}

			_preview = document;
		}

		_documents.Insert(at, document);
		_views.Insert(at, view);
		_panes.Insert(at, null);
		ActiveIndex = at;
		DocumentAdded?.Invoke(document);
		TouchHistory();
		SyncBreadcrumbs();
	}

	private Document? _preview;
	private bool _openingPreview;

	private static bool PreviewEnabled => Settings.Current.GetBoolean("workbench.editor.enablePreview") ?? true;

	public int PreviewIndex => _preview is { } preview ? _documents.IndexOf(preview) : -1;

	public void Pin(int index) {
		if (index >= 0 && index < _documents.Count && ReferenceEquals(_documents[index], _preview)) _preview = null;
	}

	public void PinDirtyPreview() {
		if (_preview is { } preview && preview.IsDirty()) _preview = null;
	}

	public void OpenFile(string path, bool preview) {
		bool previous = _openingPreview;
		_openingPreview = preview;

		try {
			OpenFile(path);

			if (!preview) Pin(ActiveIndex);
		}
		finally {
			_openingPreview = previous;
		}
	}

	private T AsPreview<T>(bool preview, Func<T> open) {
		bool previous = _openingPreview;
		_openingPreview = preview;

		try {
			return open();
		}
		finally {
			_openingPreview = previous;
		}
	}

	public void ReloadExternalChanges(IReadOnlyCollection<string> directories) {
		bool changed = false;

		foreach (Document document in _documents) {
			if (document.Path is not { } path || Path.GetDirectoryName(path) is not { } directory) continue;

			bool watched = false;

			foreach (string candidate in directories) {
				if (string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), directory, StringComparison.OrdinalIgnoreCase)) {
					watched = true;
					break;
				}
			}

			if (watched) changed |= document.ReloadIfChangedOnDisk();
		}

		foreach (Document document in _documents) {
			if (document.Source is not { } source || _documents.Contains(source) || source.Path is not { } sourcePath || Path.GetDirectoryName(sourcePath) is not { } sourceDirectory) continue;

			foreach (string candidate in directories) {
				if (!string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), sourceDirectory, StringComparison.OrdinalIgnoreCase)) continue;

				changed |= source.ReloadIfChangedOnDisk();
				break;
			}
		}

		if (changed) ActiveDocumentChanged?.Invoke();
	}

	private static string VirtualFileName(string uri) {
		ReadOnlySpan<char> path = uri.AsSpan();
		int query = path.IndexOfAny('?', '#');

		if (query >= 0) path = path[..query];

		return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..].ToString());
	}

	public event Action<Document>? DocumentAdded;

	public event Action<Document>? DocumentRemoved;

	public event Action<Document>? DocumentSaving;

	public event Action<Document>? DocumentSaved;

	public bool IsPaneAt(int index) => (uint)index < (uint)_panes.Count && _panes[index] is not null;

	public int IndexOf(Document document) => _documents.IndexOf(document);

	public Document? FindVirtual(string uri) {
		foreach (Document document in _documents) {
			if (string.Equals(document.VirtualUri, uri, StringComparison.Ordinal)) return document;
		}

		return null;
	}

	public void OpenVirtual(Document document) {
		int existing = _documents.IndexOf(document);

		if (existing >= 0) {
			Activate(existing);
			return;
		}

		Open(document);
	}

	public void ShowDiff(Document document, string originalKey, string originalText, float previousLineHeight, float previousViewport) {
		document.DiffOriginalUri = originalKey;
		document.DiffOriginalText = originalText;

		int index = _documents.IndexOf(document);

		if (index < 0) return;

		TextView view = ViewAt(index);
		view.DiffBase = null;
		view.Diff = null;

		if (_diffEditors.Remove(view, out DiffEditor? previous)) previous.Detach();

		DiffEditor diff = new(view, originalText, document.SourcePath ?? document.Name, originalKey.EndsWith("?~", StringComparison.Ordinal)) { HideUnchangedRegions = Settings.Current.GetBoolean("diffEditor.hideUnchangedRegions.enabled") ?? false, ShowMoves = Settings.Current.GetBoolean("diffEditor.experimental.showMoves") ?? false };
		diff.StageHunk = hunk => StageHunk(document, diff, hunk);
		_diffEditors[view] = diff;

		if (LineDiff.Compute(originalText, document.Buffer.GetText()) is not [var first, ..]) return;

		TextSnapshot snapshot = view.Snapshot;
		int line = Math.Clamp(first.ModifiedStart, 0, Math.Max(0, snapshot.LineCount - 1));
		view.MoveTo(snapshot.GetLineSpan(line).Start, extend: false);

		float lineHeight = view.LastViewportHeight > 0f ? view.LastLineHeight : previousLineHeight;
		float viewport = view.LastViewportHeight > 0f ? view.LastViewportHeight : previousViewport;

		if (lineHeight > 0f) diff.Sync(lineHeight, _scale);

		if (lineHeight > 0f && viewport > 0f) view.RevealLineInCenter(line, lineHeight, viewport);
		else RevealCaret?.Invoke();
	}

	private readonly Dictionary<TextView, DiffEditor> _diffEditors = [];

	public TextView? FocusedView => ActiveDiff is { OriginalFocused: true } diff ? diff.Original : ActiveView;

	public DiffEditor? ActiveDiff => ActiveView is { } view && _diffEditors.TryGetValue(view, out DiffEditor? diff) && !diff.Inline ? diff : null;

	private void StageHunk(Document document, DiffEditor diff, LineHunk hunk) {
		if (Repository is not { } repository || document.SourcePath is not { } path) return;

		string root = repository.GetRootPath();
		string relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
		string staged = diff.OriginalWithHunk(hunk);
		string? entry = Git.GitProcess.Run(root, "ls-files", "-s", "--", relative);
		string mode = entry is { Length: > 6 } ? entry[..6] : "100644";

		if (Git.GitProcess.RunWithInput(root, staged, "hash-object", "-w", "--stdin", "--path", relative)?.Trim() is not { Length: > 0 } hash) return;

		if (Git.GitProcess.Run(root, "update-index", "--add", "--cacheinfo", mode + "," + hash + "," + relative) is null) return;

		diff.ReplaceOriginal(staged);
		document.DiffOriginalText = staged;
		ThreadPool.UnsafeQueueUserWorkItem(_ => repository.Refresh(), null);
	}

	public void OpenGitChange(string path, GitFileStatus status, bool staged, bool preview = true) => AsPreview(preview, () => {
		OpenGitChangeCore(path, status, staged);

		if (!preview) Pin(ActiveIndex);

		return true;
	});

	private void OpenGitChangeCore(string path, GitFileStatus status, bool staged) {
		if (Repository is not { } repository) return;

		string root = repository.GetRootPath();
		string relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
		string name = System.IO.Path.GetFileName(path);
		float previousLineHeight = ActiveView?.LastLineHeight ?? 0f;
		float previousViewport = ActiveView?.LastViewportHeight ?? 0f;

		if (status == GitFileStatus.Untracked || (status == GitFileStatus.Conflicted && !staged)) {
			OpenFile(path);
			return;
		}

		if (status == GitFileStatus.Deleted) {
			string reference = staged ? "HEAD" : string.Empty;
			string uri = GitUri(relative, "deleted" + reference);

			if (FindVirtual(uri) is not { } deleted) deleted = Document.Virtual(uri, name + (staged ? " (Index)" : " (Deleted)"), string.Empty);

			OpenVirtual(deleted);
			deleted.Buffer.ReadOnly = true;
			ViewAt(_documents.IndexOf(deleted)).Highlighter.SetLanguage(Forge.Syntax.Languages.ForPath(path));
			string before = GitText(root, (staged ? "HEAD:" : ":") + relative);

			if (before.Length == 0 && !staged) before = GitText(root, "HEAD:" + relative);

			ShowDiff(deleted, GitUri(relative, reference), before, previousLineHeight, previousViewport);
			return;
		}

		if (staged) {
			Document? index = OpenGitVersion(root, relative, string.Empty, name + " (Index)");

			if (index is null || status == GitFileStatus.Added) return;

			ShowDiff(index, GitUri(relative, "HEAD"), GitText(root, "HEAD:" + relative), previousLineHeight, previousViewport);
			return;
		}

		if (OpenDiffTab(path, GitUri(relative, "~"), name + " (Working Tree)") is not { } document) return;

		ShowDiff(document, GitUri(relative, "~"), GitText(root, ":" + relative), previousLineHeight, previousViewport);
	}

	public void OpenCommitChange(GitCommit commit, GitCommitChange change) => AsPreview(true, () => {
		if (Repository is not { } repository) return false;

		string root = repository.GetRootPath();
		string relative = System.IO.Path.GetRelativePath(root, change.Path).Replace('\\', '/');
		string originalRelative = change.OriginalPath is { } previous ? System.IO.Path.GetRelativePath(root, previous).Replace('\\', '/') : relative;
		string parent = commit.Parents.Length > 0 ? commit.Parents[0] : string.Empty;
		string shortParent = parent.Length > 7 ? parent[..7] : parent;
		string title = System.IO.Path.GetFileName(change.Path) + " (" + (shortParent.Length > 0 ? shortParent : commit.ShortId + "^") + " ↔ " + commit.ShortId + ")";
		string uri = GitUri(relative, commit.Id);
		float previousLineHeight = ActiveView?.LastLineHeight ?? 0f;
		float previousViewport = ActiveView?.LastViewportHeight ?? 0f;

		if (FindVirtual(uri) is not { } document) document = Document.Virtual(uri, title, change.Status == GitFileStatus.Deleted ? string.Empty : GitText(root, commit.Id + ":" + relative));

		OpenVirtual(document);
		document.Buffer.ReadOnly = true;
		ViewAt(_documents.IndexOf(document)).Highlighter.SetLanguage(Forge.Syntax.Languages.ForPath(change.Path));

		string before = parent.Length == 0 || change.Status == GitFileStatus.Added ? string.Empty : GitText(root, parent + ":" + originalRelative);
		ShowDiff(document, GitUri(originalRelative, parent), before, previousLineHeight, previousViewport);
		return true;
	});

	public Document? OpenDiffTab(string path, string originalKey, string title, bool preview) => AsPreview(preview, () => OpenDiffTab(path, originalKey, title));

	public Document? OpenDiffTab(string path, string originalKey, string title) {
		if (FindDocument(path) is not { } source) {
			try {
				source = Document.Open(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				return null;
			}
		}

		source.ReloadIfChangedOnDisk();

		string uri = "diff:" + originalKey + "|" + path;

		if (FindVirtual(uri) is { } existing) {
			OpenVirtual(existing);
			return existing;
		}

		Document diff = Document.DiffOf(source, uri, title);
		Open(diff);
		ViewAt(_documents.Count - 1).Highlighter.SetLanguage(Forge.Syntax.Languages.ForPath(path));
		return diff;
	}

	private Document? OpenGitVersion(string root, string relative, string reference, string title) {
		string uri = GitUri(relative, reference);

		if (FindVirtual(uri) is not { } document) {
			document = Document.Virtual(uri, title, GitText(root, (reference.Length == 0 ? ":" : reference + ":") + relative));
		}

		OpenVirtual(document);
		return document;
	}

	private Document? FindDocument(string path) {
		foreach (Document document in _documents) {
			if (string.Equals(document.Path, path, StringComparison.OrdinalIgnoreCase)) return document;
		}

		return null;
	}

	private static string GitUri(string relative, string reference) => "git:/" + relative + "?" + reference;

	private static string GitText(string root, string spec) {
		string text = Git.GitProcess.Run(root, "show", spec) ?? string.Empty;
		return text is ['\uFEFF', ..] ? text[1..] : text;
	}

	public bool Save(Document document) {
		int index = _documents.IndexOf(document);

		if (index < 0) return document.Save();

		int active = ActiveIndex;
		ActiveIndex = index;
		bool saved = SaveActive();

		if (active >= 0 && active < _documents.Count) ActiveIndex = active;

		return saved;
	}

	public void CaptureViewStates() {
		for (int i = 0; i < _documents.Count; i++) CaptureViewState(i);
	}

	private void CaptureViewState(int index) {
		if (_panes[index] is not null || _documents[index].Path is not { } path) return;

		TextView view = _views[index];
		ViewStates[path] = new EditorViewState(view.SelectionAnchor, view.CaretOffset, view.ScrollY);
	}

	private void TouchHistory() {
		if (ActiveDocument?.Path is { } path) QuickOpen.Touch(path);
	}

	public void OpenFile(string path) {
		for (int i = 0; i < _documents.Count; i++) {
			if (string.Equals(_documents[i].Path, path, StringComparison.OrdinalIgnoreCase)) {
				Activate(i);
				return;
			}
		}

		if (FindPane(path) is not null) {
			OpenPane(FindPane(path)!);
			return;
		}

		if (ImageLoader is { } loader && IsPreviewableImage(path)) {
			OpenPane(new ImagePreviewPane(path, loader));
			Sidebar.SelectedPath = path;
			return;
		}

		if (IsBinary(path)) {
			OpenPane(new BinaryFilePane(path, OpenAsText));
			Sidebar.SelectedPath = path;
			return;
		}

		OpenAsText(path);
	}

	public Func<string, IImage?>? ImageLoader { get; set; }

	private static readonly string[] PreviewImageExtensions = [".jpg", ".jpe", ".jpeg", ".jfif", ".png", ".bmp", ".dib", ".gif", ".ico", ".cur", ".webp", ".avif", ".svg", ".tif", ".tiff", ".dds", ".jxr", ".wdp", ".hdp", ".heic", ".heif", ".jxl"];

	private static bool IsPreviewableImage(ReadOnlySpan<char> path) {
		ReadOnlySpan<char> extension = Path.GetExtension(path);

		foreach (string candidate in PreviewImageExtensions) {
			if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
		}

		return false;
	}

	private const int BinaryDetectionBytes = 512;

	private static bool IsBinary(string path) {
		Span<byte> buffer = stackalloc byte[BinaryDetectionBytes];
		int read;

		try {
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
			read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return false;
		}

		Span<byte> head = buffer[..read];

		if (head is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]) return false;

		return head.Contains((byte)0);
	}

	private void OpenAsText(string path) {
		int existing = -1;

		for (int i = 0; i < _panes.Count; i++) {
			if (_panes[i] is BinaryFilePane pane && string.Equals(pane.Id, path, StringComparison.OrdinalIgnoreCase)) existing = i;
		}

		if (existing >= 0) Close(existing);

		try {
			Open(Document.Open(path));
			Sidebar.SelectedPath = path;

			FileOpened?.Invoke(path);
		}
		catch (IOException) {
		}
		catch (UnauthorizedAccessException) {
		}
	}

	public void SetSemanticTokens(string path, IReadOnlyList<SemanticToken> tokens) {
		for (int i = 0; i < _documents.Count; i++) {
			if (!string.Equals(_documents[i].Path, path, StringComparison.OrdinalIgnoreCase)) continue;

			TextView.SemanticSpan[] spans = new TextView.SemanticSpan[tokens.Count];

			for (int t = 0; t < tokens.Count; t++) {
				SemanticToken token = tokens[t];

				spans[t] = new TextView.SemanticSpan(token.Line - 1, token.StartColumn,
					token.Length, token.Color);
			}

			_views[i].Semantics = spans;
			return;
		}
	}

	public int IndexOf(string path) {
		for (int i = 0; i < _documents.Count; i++) {
			if (string.Equals(_documents[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
		}

		return -1;
	}

	public TextView ViewAt(int index) => _views[index];

	public void RestoreEditorFocus() => FocusEditor?.Invoke();

	public void ShowReferences(string title, IReadOnlyList<HostLocation> locations) => Panel.SetReferences(title, locations);

	public void PeekReferences(string title, int line, int column, IReadOnlyList<HostLocation> locations) {
		if (Peek is { } peek) peek.Show(title, line, column, locations);
		else ShowReferences(title, locations);
	}

	public bool ApplyWorkspaceEdit(WorkspaceEdit edit) {
		bool applied = new WorkspaceEditApplier(this).Apply(edit);

		Repository?.Refresh();
		ActiveDocumentChanged?.Invoke();
		return applied;
	}

	public void OpenMatch(string path, int line, int column, int length, bool preview = false) {
		History.Observe();
		History.MarkJump();
		OpenFile(path, preview);

		if (ActiveView is not { } view) return;

		int index = Math.Clamp(line, 0, Math.Max(0, view.Snapshot.LineCount - 1));
		Forge.Text.TextSpan span = view.Snapshot.GetLineSpan(index);
		int start = Math.Clamp(span.Start + column, span.Start, span.End);

		view.PushUndoStop();
		view.MoveTo(start, extend: false);
		view.MoveTo(Math.Clamp(start + length, start, view.Snapshot.Length), extend: true);

		if (view.LastLineHeight > 0f && view.LastViewportHeight > 0f) view.RevealLineInCenter(index, view.LastLineHeight, view.LastViewportHeight);
		else RevealCaret?.Invoke();
	}

	public void ReplaceInFile(string path, IReadOnlyList<(Forge.Search.TextMatch Match, string Replacement)> edits) {
		Document? open = null;

		foreach (Document document in _documents) {
			if (string.Equals(document.Path, path, StringComparison.OrdinalIgnoreCase)) open = document;
		}

		System.Text.Encoding encoding = new System.Text.UTF8Encoding(false);
		Forge.Text.TextBuffer buffer = open?.Buffer ?? new Forge.Text.TextBuffer(ReadText(path, out encoding));
		ApplyMatchEdits(buffer, edits);

		if (open is not null) return;

		try {
			File.WriteAllText(path, buffer.GetText(), encoding);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	public void OpenReplacePreview(string path, IReadOnlyList<(Forge.Search.TextMatch Match, string Replacement)> edits, int line) {
		Document? open = null;

		foreach (Document document in _documents) {
			if (string.Equals(document.Path, path, StringComparison.OrdinalIgnoreCase)) open = document;
		}

		string original = open?.Buffer.GetText() ?? ReadText(path, out _);
		Forge.Text.TextBuffer replaced = new(original);
		ApplyMatchEdits(replaced, edits);

		string name = System.IO.Path.GetFileName(path);
		string uri = "search-preview:/" + path.Replace('\\', '/');
		float previousLineHeight = ActiveView?.LastLineHeight ?? 0f;
		float previousViewport = ActiveView?.LastViewportHeight ?? 0f;

		if (FindVirtual(uri) is { } existing) {
			existing.Buffer.ReplaceChanged(existing.Buffer.GetText(), replaced.GetText());
			OpenVirtual(existing);
			ShowDiff(existing, "file:/" + path, original, previousLineHeight, previousViewport);
			return;
		}

		Document preview = Document.Virtual(uri, name + " \u2194 " + name + " (Replace Preview)", replaced.GetText());
		OpenVirtual(preview);
		ShowDiff(preview, "file:/" + path, original, previousLineHeight, previousViewport);
	}

	private static void ApplyMatchEdits(Forge.Text.TextBuffer buffer, IReadOnlyList<(Forge.Search.TextMatch Match, string Replacement)> edits) {
		Forge.Text.TextSnapshot snapshot = buffer.CreateSnapshot();
		List<(int Offset, int Length, string Text)> spans = new(edits.Count);

		foreach ((Forge.Search.TextMatch match, string replacement) in edits) {
			if (match.Line >= snapshot.LineCount) continue;

			Forge.Text.TextSpan line = snapshot.GetLineSpan(match.Line);
			int start = Math.Min(line.Start + match.Column, line.End);
			spans.Add((start, Math.Min(match.Length, line.End - start), replacement));
		}

		spans.Sort(static (a, b) => b.Offset.CompareTo(a.Offset));
		buffer.PushStackElement();

		foreach ((int offset, int length, string text) in spans) buffer.Replace(offset, length, text);

		buffer.PushStackElement();
	}

	private static string ReadText(string path, out System.Text.Encoding encoding) {
		try {
			using StreamReader reader = new(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			string text = reader.ReadToEnd();
			encoding = reader.CurrentEncoding;
			return text;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			encoding = new System.Text.UTF8Encoding(false);
			return string.Empty;
		}
	}

	public void GoTo(string path, int line, int column) {
		History.Observe();
		History.MarkJump();
		OpenFile(path);

		if (ActiveView is not { } view) return;

		int index = Math.Clamp(line - 1, 0, Math.Max(0, view.Snapshot.LineCount - 1));
		Forge.Text.TextSpan span = view.Snapshot.GetLineSpan(index);

		view.PushUndoStop();
		view.MoveTo(Math.Clamp(span.Start + Math.Max(0, column - 1), span.Start, span.End), extend: false);
		RevealCaret?.Invoke();
	}

	public bool EditActiveDocument(Func<string, string?> transform) {
		if (ActiveDocument is not { } document) return false;

		string current = document.Buffer.GetText(0, document.Buffer.Length);

		if (transform(current) is not { } replacement || replacement == current) return false;

		document.Buffer.ReplaceChanged(current, replacement);

		ActiveDocumentChanged?.Invoke();
		return true;
	}

	public void Activate(int index) {
		if (index >= 0 && index < _documents.Count) {
			ActiveIndex = index;
			Sidebar.SelectedPath = _documents[index].Path;
			TouchHistory();
			SyncBreadcrumbs();
		}
	}

	public void Close(int index) {
		if (index < 0 || index >= _documents.Count) return;

		bool wasLarge = _documents[index].Buffer.Length >= LargeDocumentChars;

		CaptureViewState(index);

		if (_panes[index] is null && _documents[index].Path is { } closedPath && _publishedVersions.Remove(closedPath)) PublishDocument(DocumentEvent.Closed, closedPath, null);

		EditorPane? closedPane = _panes[index];

		if (closedPane is null) DocumentRemoved?.Invoke(_documents[index]);

		if (closedPane is null && _documents[index].Path is { } recent) {
			_recentlyClosed.Remove(recent);
			_recentlyClosed.Add(recent);

			if (_recentlyClosed.Count > MaxRecentlyClosed) _recentlyClosed.RemoveAt(0);
		}

		if (ReferenceEquals(_documents[index], _preview)) _preview = null;

		_documents.RemoveAt(index);
		if (_diffEditors.Remove(_views[index], out DiffEditor? closedDiff)) closedDiff.Detach();

		_views.RemoveAt(index);
		_panes.RemoveAt(index);

		if (closedPane is not null) {
			closedPane.Parent?.RemoveChild(closedPane);
			Views?.Forget(closedPane);
			closedPane.OnClosed();
		}

		if (wasLarge) {
			System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
				System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;

			GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
		}

		ActiveIndex = _documents.Count == 0 ? -1 : Math.Min(index, _documents.Count - 1);
		Sidebar.SelectedPath = ActiveDocument?.Path;
		SyncBreadcrumbs();
	}

	public void CloseActive() => Close(ActiveIndex);

	public void PathMoved(string from, string to) {
		string prefix = from.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

		foreach (Document document in _documents) {
			if (document.Path is not { } path) continue;

			if (path.Equals(from, StringComparison.OrdinalIgnoreCase)) document.Moved(to);
			else if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) document.Moved(System.IO.Path.Combine(to, path[prefix.Length..]));
		}

		Tabs.Repaint();
	}

	public void PathDeleted(string deleted) {
		string prefix = deleted.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

		for (int i = _documents.Count - 1; i >= 0; i--) {
			if (_documents[i].Path is { } path && !_documents[i].IsDirty() && (path.Equals(deleted, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) Close(i);
		}
	}

	public void CompareFiles(string original, string modified) {
		string name = System.IO.Path.GetFileName(original) + " ↔ " + System.IO.Path.GetFileName(modified);
		float previousLineHeight = ActiveView?.LastLineHeight ?? 0f;
		float previousViewport = ActiveView?.LastViewportHeight ?? 0f;

		if (OpenDiffTab(modified, "compare:" + original, name, false) is not { } document) return;

		string text;

		try {
			text = File.ReadAllText(original);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return;
		}

		ShowDiff(document, "file:" + original, text, previousLineHeight, previousViewport);
	}

	private const int MaxRecentlyClosed = 20;

	private readonly List<string> _recentlyClosed = [];

	public void CloseAll() {
		for (int i = _documents.Count - 1; i >= 0; i--) Close(i);
	}

	public void CloseOthers(int keep) {
		if (keep < 0 || keep >= _documents.Count) return;

		for (int i = _documents.Count - 1; i > keep; i--) Close(i);

		for (int i = keep - 1; i >= 0; i--) Close(i);

		Activate(0);
	}

	public void CloseToTheRight(int index) {
		for (int i = _documents.Count - 1; i > index; i--) Close(i);

		Activate(Math.Min(index, _documents.Count - 1));
	}

	public void CloseToTheLeft(int index) {
		if (index <= 0 || index >= _documents.Count) return;

		for (int i = index - 1; i >= 0; i--) Close(i);

		Activate(0);
	}

	public void CloseSaved() {
		int active = ActiveIndex;
		Document? keep = active >= 0 ? _documents[active] : null;

		for (int i = _documents.Count - 1; i >= 0; i--) {
			if (_panes[i] is null && !_documents[i].IsDirty()) Close(i);
		}

		if (keep is not null && _documents.IndexOf(keep) is >= 0 and int found) Activate(found);
	}

	public void ReopenClosed() {
		while (_recentlyClosed.Count > 0) {
			string path = _recentlyClosed[^1];
			_recentlyClosed.RemoveAt(_recentlyClosed.Count - 1);

			if (!File.Exists(path)) continue;

			OpenFile(path);
			return;
		}
	}

	public void ActivateRelative(int delta) {
		if (_documents.Count == 0) return;

		Activate(((ActiveIndex + delta) % _documents.Count + _documents.Count) % _documents.Count);
	}

	public static string AutoSaveMode => Settings.Current.GetString("files.autoSave") ?? "off";

	private Document? _lastFocused;

	public void AutoSave(Document? only = null) {
		for (int i = _documents.Count - 1; i >= 0; i--) {
			if (i >= _documents.Count) continue;

			Document document = _documents[i];

			if (_panes[i] is not null || (only is not null && !ReferenceEquals(document, only) && !ReferenceEquals(document.Source, only)) || !document.IsDirty() || document.SourcePath is null) continue;

			Save(document);
		}
	}

	public void TrackFocusChange() {
		Document? current = ActiveDocument;

		if (ReferenceEquals(current, _lastFocused)) return;

		Document? previous = _lastFocused;
		_lastFocused = current;

		if (previous is not null && AutoSaveMode == "onFocusChange" && _documents.Contains(previous)) AutoSave(previous);
	}

	public void SaveAll() {
		int active = ActiveIndex;

		for (int i = 0; i < _documents.Count; i++) {
			if (_panes[i] is not null || !_documents[i].IsDirty()) continue;

			ActiveIndex = i;
			SaveActive();
		}

		if (active >= 0 && active < _documents.Count) ActiveIndex = active;

		ActiveDocumentChanged?.Invoke();
	}

	public void RevertActive() {
		if (ActiveDocument is not { Path: { } path } document || !File.Exists(path)) return;

		document.Buffer.BeginEdit();
		document.Buffer.Replace(0, document.Buffer.Length, File.ReadAllText(path));
		document.Buffer.EndEdit();
		document.MarkSaved();
		ActiveDocumentChanged?.Invoke();
	}

	public Func<string, string?>? WillSave { get; set; }

	public bool SaveActive() {
		if (WillSave is { } willSave && ActiveDocument?.Path is not null) EditActiveDocument(willSave);

		if (ActiveDocument is { } saving) DocumentSaving?.Invoke(saving);

		if (ActiveDocument is not { } document || !document.Save()) return false;

		DocumentSaved?.Invoke(document);

		if (document.Path is { } savedPath && _documentObservers.Length > 0) PublishDocument(DocumentEvent.Saved, savedPath, document.Buffer.GetText());

		Repository?.Refresh();
		ActiveDocumentChanged?.Invoke();
		return true;
	}

	public Action? ActiveDocumentChanged;

	public Action<string>? FileOpened;

	private void SyncBreadcrumbs() {
		Breadcrumbs.SetPath(RootPath, ActiveDocument?.Path);
		ActiveDocumentChanged?.Invoke();
	}

	public void Dispose() => Panel.Dispose();

	#endregion

	private bool FloatingWidgetOpen =>
		Find.IsOpen ||
		IntelliSense is { Suggest.IsOpen: true } or { Signature.IsOpen: true } or { Hover.IsOpen: true };

	private bool _floatingLastFrame;
	private int _sourceControlBadge = -1;

	public void UpdateBeforeFrame() {

		PublishActiveDocument();

		IntelliSense?.Tick();
		History.Observe();

		StatusBar.Errors = Panel.ErrorCount;
		StatusBar.Warnings = Panel.WarningCount;
		StatusBar.Sync(ActiveView, BranchName);
		Sidebar.SourceControl.SyncIfStale();
		Sidebar.Graph.SyncIfStale();

		int changes = Repository is { } counted ? counted.GetStaged().Count + counted.GetUnstaged().Count + counted.GetMerge().Count : 0;

		if (changes != _sourceControlBadge) {
			_sourceControlBadge = changes;
			Views?.SetBadge(Sidebar.SourceControlId, changes);
		}

		ActivityBar.SidebarVisible = ShowSidebar;
		ActivityBar.PanelVisible = Panel.IsOpen;
		ActivityBar.AuxiliaryBarVisible = AuxiliaryWidth > 0f;

		Presence?.Update();

		_floatingLastFrame = FloatingWidgetOpen;
	}

	public void RenderEditor(IPainter painter, float left, float top, float width, float height) {
		if (ActiveView is not { } view) {
			DrawEmptyState(painter, left, top, width, height);
			return;
		}

		if (_diffEditors.TryGetValue(view, out DiffEditor? diff)) {
			diff.SetInline(width < DiffEditor.InlineBreakpoint * _scale);

			if (!diff.Inline) {
				view.Scale = _scale;
				diff.Render(painter, left, top, width, height, _scale);
				return;
			}
		}

		float textWidth = Math.Max(0f, width - (ShowMinimap ? Minimap.Width(_scale) : 0f));

		if (TrailingSpaces is { } trailing) {
			float lineHeight = painter.GetCode().GetLineHeight();
			int firstLine = view.LineAtY(view.ScrollY, lineHeight);
			Decorations.UpdateVisible(view, trailing, IntelliSense?.Occurrences.For(ActiveFilePath, view.Buffer.Version) ?? [], firstLine, firstLine + (int)(height / lineHeight) + 1);
		}

		view.Lightbulb = IntelliSense?.Actions.Current is { } bulb ? new TextView.LightbulbMark(bulb.Line, bulb.Column, bulb.InGutter, bulb.AutoFix) : null;
		view.BreakpointProvider = BreakpointMarkFor;
		view.ExecutionLine = ExecutionLineFor?.Invoke(ActiveFilePath ?? string.Empty) ?? -1;
		view.Scale = _scale;
		view.Render(painter, left, top, textWidth, height);

		if (ShowMinimap && painter.NeedsPaint(left + textWidth, top, Minimap.Width(_scale), height)) _minimap.Render(painter, view, view.Highlighter, left + textWidth, top, Minimap.Width(_scale), height, _scale);
	}

	private static void DrawEmptyState(IPainter painter, float x, float y, float width, float height) {
		painter.FillRectangle(x, y, width, height, Theme.EditorBackground);

		ITextFont font = painter.GetUi();
		ReadOnlySpan<char> message = "Ctrl+P to open a file";

		float textX = x + ((width - font.Measure(message)) / 2f);
		painter.DrawText(font, message, textX, y + (height / 2f), Theme.SectionHeaderForeground);
	}
}
