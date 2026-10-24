using Forge.VSCode;
using Forge.Commands;
using Forge.Common.Commands;
using Forge.Common.Debug;
using Forge.Common.Editor;
using Forge.Common.Extensions;
using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Core;
using Forge.Editor;
using Forge.Extensions.Discord;
using Forge.Extensions.TrailingSpaces;
using Forge.Extensions;
using Forge.Git;
using Forge.Language;
using Forge.Render;
using Forge.Search;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.UI;
using Forge.Workspace;

namespace Forge.Shell;

public sealed class Workbench(float scale) : IEditorHost, IDisposable
{
	private readonly List<Document> _documents = [];
	private readonly List<TextView> _views = [];
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

	public QuickOpen QuickOpen { get; } = new();

	public MenuPopup Menu { get; } = new();

	public FindWidget Find { get; } = new();

	public Panel Panel { get; } = new();

	public AuxiliaryBar Auxiliary { get; } = new();

	public Common.Webview.IWebviewHost Webviews { get; set; } = Common.Webview.NullWebviewHost.Instance;

	public IntelliSense? IntelliSense { get; set; }

	public string? ActiveFilePath => ActiveDocument?.Path;

	public string? GetActiveFilePath() => ActiveFilePath;

	public IDocument? GetActiveDocument() => ActiveDocument;

	public ITextView? GetActiveView() => ActiveView;

	public IReadOnlyList<Document> Documents => _documents;

	public int ActiveIndex { get; private set; } = -1;

	public Document? ActiveDocument => ActiveIndex >= 0 ? _documents[ActiveIndex] : null;

	public TextView? ActiveView => ActiveIndex >= 0 ? _views[ActiveIndex] : null;

	public bool ShowSidebar { get; set; } = true;

	public bool PanelFocused { get; set; }

	public ProcessExplorer? ProcessExplorer { get; set; }

	public bool ShowMinimap { get; set; } = true;

	public string? RootPath { get; set; }

	public string? BranchName { get; set; }

	public IGitRepository? Repository { get; set; }

	public IExtensionRegistry? Extensions { get; set; }

	public IExtensionHost? ExtensionHost { get; set; }

	public VSCodeBridge? VSCode { get; set; }

	public Action<string>? CommandInvoked;

	public bool RunCommand(string id, ICommandHost host) {
		CommandInvoked?.Invoke(id);
		return Registry.Execute(id, host);
	}

	public LaunchPicker LaunchPicker { get; } = new();

	public Func<int, BreakpointMark>? BreakpointMarkFor { get; set; }

	public Func<string, int>? ExecutionLineFor { get; set; }

	public CommandPalette Palette { get; } = new();

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
		if (_documentObservers.Length == 0 || ActiveDocument is not { Path: { } path } document) return;

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

	public Action? PaintBackdrop { get; init; }

	public bool ShowFrameStats { get; set; }

	public IFrameStats? FrameStats { get; set; }

	#region Layout

	public float ActivityBarWidth => ActivityBar.Width(_scale);

	public float SidebarWidth => ShowSidebar ? Sidebar.Width * _scale : 0f;

	private float ContentLeft => ActivityBarWidth + SidebarWidth;

	public float EditorLeft => ContentLeft + (Metrics.CardMargin * _scale);

	private float EditorRightGutter => Metrics.CardMargin * 2f * _scale;

	public float AuxiliaryWidth => Auxiliary is { IsOpen: true, HasViews: true } ? Auxiliary.Width * _scale : 0f;

	public float AuxiliaryStripWidth => Auxiliary.HasViews ? AuxiliaryBar.StripWidth(_scale) : 0f;

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

	public void Open(Document document) {
		_documents.Add(document);
		_views.Add(new TextView(document.Buffer, document.Path) { Diff = Diff });
		ActiveIndex = _documents.Count - 1;
		SyncBreadcrumbs();
	}

	public void OpenFile(string path) {
		for (int i = 0; i < _documents.Count; i++) {
			if (string.Equals(_documents[i].Path, path, StringComparison.OrdinalIgnoreCase)) {
				Activate(i);
				return;
			}
		}

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

	public void GoTo(string path, int line, int column) {
		OpenFile(path);

		if (ActiveView is not { } view) return;

		int index = Math.Clamp(line - 1, 0, Math.Max(0, view.Snapshot.LineCount - 1));
		Forge.Text.TextSpan span = view.Snapshot.GetLineSpan(index);

		view.MoveTo(Math.Clamp(span.Start + Math.Max(0, column - 1), span.Start, span.End),
			extend: false);
	}

	public bool EditActiveDocument(Func<string, string?> transform) {
		if (ActiveDocument is not { } document) return false;

		string current = document.Buffer.GetText(0, document.Buffer.Length);

		if (transform(current) is not { } replacement || replacement == current) return false;

		document.Buffer.BeginEdit();
		document.Buffer.Replace(0, document.Buffer.Length, replacement);
		document.Buffer.EndEdit();

		ActiveDocumentChanged?.Invoke();
		return true;
	}

	public void Activate(int index) {
		if (index >= 0 && index < _documents.Count) {
			ActiveIndex = index;
			Sidebar.SelectedPath = _documents[index].Path;
			SyncBreadcrumbs();
		}
	}

	public void Close(int index) {
		if (index < 0 || index >= _documents.Count) return;

		bool wasLarge = _documents[index].Buffer.Length >= LargeDocumentChars;

		_documents.RemoveAt(index);
		_views.RemoveAt(index);

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

	public Func<string, string?>? WillSave { get; set; }

	public bool SaveActive() {
		if (WillSave is { } willSave) EditActiveDocument(willSave);

		if (ActiveDocument is not { } document || !document.Save()) return false;

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

	public void UpdateBeforeFrame() {
		if (TrailingSpaces is { } trailing) Decorations.UpdateIfStale(ActiveView, trailing);

		PublishActiveDocument();

		IntelliSense?.Tick();

		StatusBar.Errors = 0;
		StatusBar.Warnings = 0;

		foreach (Problem problem in Panel.Problems) {
			if (problem.IsError) StatusBar.Errors++;
			else StatusBar.Warnings++;
		}

		Presence?.Update();

		_floatingLastFrame = FloatingWidgetOpen;
	}

	public void Render(IPainter painter, float width, float height) {
		float statusTop = StatusBarTop(height);
		float editorLeft = EditorLeft;
		float editorWidth = EditorWidth(width);
		float editorHeight = EditorHeight(height);
		float textWidth = TextWidth(width);

		painter.FillRectangle(0f, ShellTop, width, height - ShellTop, Theme.EditorBackground);
		PaintBackdrop?.Invoke();

		if (painter.NeedsPaint(0f, 0f, width, ShellTop)) TitleBar.Render(painter, width, _scale);

		ActivityBar.SourceControlCount = Repository is { } counted
			? counted.GetStaged().Count + counted.GetUnstaged().Count + counted.GetMerge().Count
			: 0;

		ActivityBar.SidebarVisible = ShowSidebar;
		ActivityBar.PanelVisible = Panel.IsOpen;
		ActivityBar.AuxiliaryBarVisible = AuxiliaryWidth > 0f;

		if (painter.NeedsPaint(0f, ShellTop, ActivityBarWidth, statusTop - ShellTop)) {
			ActivityBar.Render(painter, 0f, ShellTop, statusTop - ShellTop, _scale);
		}

		if (ShowSidebar &&
			painter.NeedsPaint(ActivityBarWidth, ShellTop, editorLeft - ActivityBarWidth,
				statusTop - ShellTop)) Sidebar.Render(painter, ActivityBarWidth, ShellTop, statusTop - ShellTop, _scale);

		float cardHeight = EditorBottom(height) - ShellTop;
		float cardRadius = Metrics.RadiusLarge * _scale;

		if (painter.NeedsPaint(editorLeft, ShellTop, editorWidth, cardHeight)) {
			painter.PushRoundedClip(editorLeft, ShellTop, editorWidth, cardHeight, cardRadius);
			painter.FillRectangle(editorLeft, ShellTop, editorWidth, cardHeight, Theme.EditorBackground);

			if (painter.NeedsPaint(editorLeft, TabsTop, editorWidth, EditorTop - TabsTop)) {
				Tabs.Render(painter, _documents, ActiveIndex, editorLeft, TabsTop, editorWidth, _scale);
			}

			if (ActiveView is { } view) {
				if (painter.NeedsPaint(editorLeft, BreadcrumbsTop, editorWidth, Breadcrumbs.Height(_scale))) {
					Breadcrumbs.Render(painter, editorLeft, BreadcrumbsTop, editorWidth, _scale);
				}

				view.BreakpointProvider = BreakpointMarkFor;
				view.ExecutionLine = ExecutionLineFor?.Invoke(ActiveFilePath ?? string.Empty) ?? -1;

				view.Render(painter, editorLeft, EditorTop, textWidth, editorHeight);

				if (ShowMinimap &&
					painter.NeedsPaint(editorLeft + textWidth, EditorTop, Minimap.Width(_scale), editorHeight)) {
					_minimap.Render(painter, view, view.Highlighter, editorLeft + textWidth, EditorTop,
						Minimap.Width(_scale), editorHeight, _scale);
				}

				Find.Render(painter, view, editorLeft + editorWidth, EditorTop, _scale);

				IntelliSense?.Render(painter, editorLeft, EditorTop, EditorBottom(height),
					editorLeft + editorWidth, _scale);
			}
			else {
				DrawEmptyState(painter, editorLeft, BreadcrumbsTop, editorWidth,
					editorHeight + Breadcrumbs.Height(_scale));
			}

			painter.PopRoundedClip();
			painter.StrokeRoundedRectangle(editorLeft, ShellTop, editorWidth, cardHeight, cardRadius,
				Metrics.StrokeThickness * _scale, Theme.PanelBorder);
		}

		if (AuxiliaryStripWidth > 0f && painter.NeedsPaint(AuxiliaryStripLeft(width), ShellTop, AuxiliaryStripWidth, statusTop - ShellTop)) Auxiliary.RenderStrip(painter, AuxiliaryStripLeft(width), ShellTop, _scale);

		if (AuxiliaryWidth > 0f) {
			float auxiliaryLeft = AuxiliaryLeft(width);

			if (painter.NeedsPaint(auxiliaryLeft, ShellTop, AuxiliaryWidth, statusTop - ShellTop)) {
				Auxiliary.Render(painter, auxiliaryLeft, ShellTop, AuxiliaryWidth, statusTop - ShellTop, _scale);
			}
		}

		if (Panel.IsOpen &&
			painter.NeedsPaint(editorLeft, PanelTop(height), editorWidth, height - PanelTop(height))) {
			Panel.Render(painter, editorLeft, PanelTop(height), editorWidth, Panel.Height * _scale, _scale);

			Panel.RenderProfileMenu(painter, _scale);
			Panel.RenderChannelMenu(painter, _scale);
		}

		if (painter.NeedsPaint(0f, statusTop, width, height - statusTop)) {
			StatusBar.Render(painter, ActiveView, BranchName, 0f, statusTop, width, _scale);
		}

		if (ShowFrameStats && FrameStats is { } stats) DrawFrameStats(painter, stats, width, statusTop);

		if (Menu.IsOpen) {
			Menu.Render(painter, width, _scale);
		}

		Sidebar.SourceControl.Menu.Render(painter, width, height, _scale);

		LaunchPicker.Render(painter, _scale);

		QuickOpen.Render(painter, width, height, _scale);
		Palette.Render(painter, width, height, _scale);
		ProcessExplorer?.Render(painter, width, height, _scale);
	}

	private static void DrawEmptyState(IPainter painter, float x, float y, float width, float height) {
		painter.FillRectangle(x, y, width, height, Theme.EditorBackground);

		ITextFont font = painter.GetUi();
		ReadOnlySpan<char> message = "Ctrl+P to open a file";

		float textX = x + ((width - font.Measure(message)) / 2f);
		painter.DrawText(font, message, textX, y + (height / 2f), Theme.SectionHeaderForeground);
	}

	private float BadgeHeight => 22f * _scale;

	private float BadgeGap => Metrics.SpaceSm * _scale;

	public PixelRect FrameStatsRect(float windowWidth, float windowHeight) {
		if (!ShowFrameStats) return PixelRect.Empty;

		float height = BadgeHeight;
		float width = 520f * _scale;
		float y = StatusBarTop(windowHeight) - height - BadgeGap;

		return PixelRect.Around(windowWidth - AuxiliaryWidth - width - (Metrics.SpaceLg * _scale), y, width,
			height);
	}

	private void DrawFrameStats(IPainter painter, IFrameStats stats, float windowWidth, float statusTop) {
		float y = statusTop - BadgeHeight - BadgeGap;
		float right = windowWidth - AuxiliaryWidth - (Metrics.SpaceLg * _scale);

		Span<char> buffer = stackalloc char[64];

		int length = FormatTriple(buffer, "drawn ", stats.GetLastDamagePercent(), stats.GetAverageDamagePercent(), stats.GetPeakDamagePercent(), "%", "F0");

		right = DrawBadge(painter, buffer[..length], right, y);

		length = 0;
		length += Write(buffer, "fps ");
		((int)stats.GetImpliedFps()).TryFormat(buffer[length..], out int digits);
		length += digits;

		right = DrawBadge(painter, buffer[..length], right, y);

		length = FormatTriple(buffer, "", stats.GetLastFrameMs(), stats.GetAverageFrameMs(), stats.GetPeakFrameMs(),
			" ms", "F2");

		DrawBadge(painter, buffer[..length], right, y);
	}

	private float DrawBadge(IPainter painter, ReadOnlySpan<char> text, float right, float y) {
		ITextFont font = painter.GetUiSmall();

		float padding = Metrics.SpaceMd * _scale;
		float width = font.Measure(text) + (padding * 2f);
		float x = right - width;

		painter.FillRoundedRectangle(x, y, width, BadgeHeight, Metrics.CornerRadius * _scale,
			Theme.WidgetBackground);

		float baseline = y + ((BadgeHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
		painter.DrawText(font, text, x + padding, baseline, Theme.AccentMuted);

		return x - BadgeGap;
	}

	private static int FormatTriple(Span<char> buffer, ReadOnlySpan<char> label, double last,
		double average, double peak, ReadOnlySpan<char> unit, ReadOnlySpan<char> format) {
		int written = Write(buffer, label);

		last.TryFormat(buffer[written..], out int digits, format);
		written += digits;
		written += Write(buffer[written..], " / ");

		average.TryFormat(buffer[written..], out digits, format);
		written += digits;
		written += Write(buffer[written..], " / ");

		peak.TryFormat(buffer[written..], out digits, format);
		written += digits;

		return written + Write(buffer[written..], unit);
	}

	private static int Write(Span<char> destination, ReadOnlySpan<char> text) {
		text.CopyTo(destination);
		return text.Length;
	}
}
