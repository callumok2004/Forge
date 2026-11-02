using System.Diagnostics;

using Forge.Common.Commands;
using Forge.Core;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Terminal;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public enum PanelView
{
	Problems,
	Output,
	DebugConsole,
	Terminal,

	References,
}

public readonly record struct Problem(string File, int Line, int Column, string Message, DiagnosticSeverity Severity, string? Code, bool Build);

public sealed class PanelPart : Panel, IDisposable, IViewPart
{
	private const float TitleHeight = 32f;
	private const float ActionSize = 22f;
	private const float ChannelSelectWidth = 180f;
	private const float ChannelSelectHeight = 20f;

	private static readonly string[] ViewTitles = ["Error List", "Output", "Debug Console", "Terminal", "References"];
	private static readonly Codicon[] ViewIcons = [Codicon.Warning, Codicon.Output, Codicon.DebugConsole, Codicon.Terminal, Codicon.References];

	private readonly List<TerminalSession> _terminals = [];
	private readonly Dictionary<TerminalSession, string> _taskNames = [];
	private readonly Panel _title;
	private readonly PanelTab[] _tabs;
	private readonly PanelPaneHeader[] _headers;
	private readonly List<List<PanelView>> _groups = [];
	private readonly float[] _weights = [1f, 1f, 1f, 1f, 1f];
	private readonly List<Splitter> _sashes = [];
	private PixelRect? _dropHighlight;
	private int _dropIndex = -1;
	private bool _dropSplit;

	private const float MinimumPaneWidth = 120f;
	private readonly Panel[] _views;
	private readonly ActionButton _newTerminal;
	private readonly ActionButton _splitTerminal;
	private readonly ActionButton _killTerminal;
	private readonly ActionButton _profiles;
	private readonly ActionButton _clearOutput;
	private readonly Button _channel;
	private readonly ActionButton _consoleSearch;
	private readonly ActionButton _consoleClear;

	private const float FilterInHeaderMinimumWidth = 600f;
	private const float FilterInHeaderMaximumHeight = 100f;
	private const float PaneFilterWidth = 200f;
	private const float TitleFilterMaxWidth = 200f;
	private const float TitleFilterMinWidth = 150f;
	private const float TitleFilterMargin = 10f;
	private const float HeaderActionSize = 20f;
	private readonly List<(ViewPaneContainer Container, PanelTab Tab)> _containers = [];

	private IReadOnlyList<TerminalProfile> _profileList = [];
	private readonly Dictionary<string, Problem[]> _problemsByOwner = [];
	private Problem[] _problems = [];
	private HostLocation[] _references = [];
	private int _activeTerminal = -1;
	private readonly List<int> _terminalGroups = [];
	private int _nextTerminalGroup;
	private volatile bool _dirty;
	private long _lastNotified;

	public PanelPart() {
		Name = "Panel";
		MouseInputEnabled = false;

		Output = Channels.Get("Forge");
		ActiveChannel = Output;

		Channels.OnAdded(() => {
			_dirty = true;
			Changed?.Invoke();
		});

		ProblemsView = new ProblemsView(this);
		OutputView = new OutputView(this) { ContextKey = ContextKeys.OutputFocus };
		TerminalView = new TerminalView(this);
		ReferencesView = new ReferencesView(this);
		DebugConsole = new DebugConsoleView();

		_title = new TitleStrip { Name = "PanelTitle", Dock = DockType.Top, MouseInputEnabled = false };

		ActionButton close = TitleAction(Codicon.Close, "Close Panel");
		close.Command = "workbench.action.togglePanel";
		_profiles = TitleAction(Codicon.ChevronDown, "Launch Profile...");
		_profiles.Action = OpenProfileMenu;
		_killTerminal = TitleAction(Codicon.Trash, "Kill Terminal");
		_killTerminal.Action = () => CloseTerminal(_activeTerminal);
		_splitTerminal = TitleAction(Codicon.SplitHorizontal, "Split Terminal");
		_splitTerminal.Action = () => NewTerminal(TerminalProfiles.Default(Profiles, DefaultProfile), split: true);
		_newTerminal = TitleAction(Codicon.Add, "New Terminal");
		_newTerminal.Action = () => NewTerminal(TerminalProfiles.Default(Profiles, DefaultProfile));
		_clearOutput = TitleAction(Codicon.ClearAll, "Clear Output");
		_clearOutput.Action = () => {
			if (Active == PanelView.DebugConsole) {
				DebugConsole.Clear();
				return;
			}

			ActiveChannel.Clear();
			OutputView.ClearSelection();
		};

		_channel = new Button { Name = "Channel", Dock = DockType.Right, Foreground = Theme.EditorForeground, HoverBackground = Theme.HoverOverlay, Radius = Metrics.RadiusSmall, Padding = new Thickness(Metrics.Space8, 0f, Metrics.Space4, 0f), Action = OpenChannelMenu };
		_title.AddChild(_channel);

		DebugConsole.Filter.Dock = DockType.None;

		_tabs = new PanelTab[ViewTitles.Length];

		for (int i = 0; i < ViewTitles.Length; i++) {
			PanelView view = (PanelView)i;
			_tabs[i] = new PanelTab(ViewTitles[i], ViewIcons[i]) { Action = () => Show(view), DragMoved = (x, y) => PreviewDrop(view, x, y), DragReleased = (x, y) => DropView(view, x, y) };
			_title.AddChild(_tabs[i]);
		}

		_headers = new PanelPaneHeader[ViewTitles.Length];

		for (int i = 0; i < ViewTitles.Length; i++) {
			PanelView view = (PanelView)i;
			_headers[i] = new PanelPaneHeader(ViewTitles[i]) { Visible = false, Clicked = () => Show(view), DragMoved = (x, y) => PreviewDrop(view, x, y), DragReleased = (x, y) => DropView(view, x, y) };
			_groups.Add([view]);
		}

		_consoleSearch = new ActionButton { Name = "DebugConsoleFind", Icon = Codicon.Search, Foreground = Theme.ChromeForeground, HoverBackground = Theme.ListHoverBackground, Radius = Metrics.RadiusSmall, Action = () => DebugConsole.Filter.RequestFocus() };
		_consoleClear = new ActionButton { Name = "DebugConsoleClear", Icon = Codicon.ClearAll, Foreground = Theme.ChromeForeground, HoverBackground = Theme.ListHoverBackground, Radius = Metrics.RadiusSmall, Action = () => DebugConsole.Clear() };
		_headers[(int)PanelView.DebugConsole].AddChild(_consoleSearch);
		_headers[(int)PanelView.DebugConsole].AddChild(_consoleClear);

		AddChild(_title);

		_views = [ProblemsView, OutputView, DebugConsole, TerminalView, ReferencesView];

		foreach (Panel view in _views) AddChild(view);

		foreach (PanelPaneHeader header in _headers) AddChild(header);

		SyncView();
	}

	private ActionButton TitleAction(Codicon icon, string name) {
		ActionButton button = new() { Name = name, Icon = icon, Dock = DockType.Right, Foreground = Theme.ChromeForeground, HoverBackground = Theme.ListHoverBackground, Radius = Metrics.RadiusSmall };
		_title.AddChild(button);
		return button;
	}

	public ProblemsView ProblemsView { get; }

	public OutputView OutputView { get; }

	public TerminalView TerminalView { get; }

	public ReferencesView ReferencesView { get; }

	public DebugConsoleView DebugConsole { get; }

	public bool IsOpen { get; set; }

	public PanelView Active { get; private set; } = PanelView.Terminal;

	public string? ActiveContainer { get; private set; }

	public IReadOnlyList<ViewPaneContainer> Containers => [.. _containers.Select(static entry => entry.Container)];

	public void Attach(ViewPaneContainer container) {
		if (_containers.Exists(entry => ReferenceEquals(entry.Container, container))) return;

		string id = container.Container.Id;
		PanelTab tab = new(container.Container.DisplayTitle, container.Container.Icon) { Action = () => ShowContainer(id), DragContainerId = container.Container.Movable ? id : null };
		container.Visible = false;
		_containers.Add((container, tab));
		_title.AddChild(tab);
		AddChild(container);
		SyncView();
	}

	public void Detach(ViewPaneContainer container) {
		int index = _containers.FindIndex(entry => ReferenceEquals(entry.Container, container));

		if (index < 0) return;

		_title.RemoveChild(_containers[index].Tab);
		RemoveChild(container);
		_containers.RemoveAt(index);

		if (ActiveContainer == container.Container.Id) ActiveContainer = null;

		SyncView();
	}

	public void ShowContainer(string? id) {
		if (id is null) return;

		IsOpen = true;
		ActiveContainer = id;
		SyncView();
	}

	public float Height { get; set; } = 240f;

	public IOutputChannel Output { get; }

	public IOutputChannel ActiveChannel { get; private set; }

	public TerminalHistory? History { get; set; }

	public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

	public string? DefaultProfile { get; set; }

	public event Action? Changed;

	public Action<string, int, int>? Navigate { get; set; }

	public IReadOnlyList<Problem> Problems => _problems;

	public IReadOnlyList<HostLocation> References => _references;

	public string ReferencesTitle { get; private set; } = "References";

	public bool HasFocusWithin => Ui?.GetFocus() is { } focus && focus.HasParent(this);

	public void FocusActive() {
		if (Active == PanelView.Terminal) TerminalView.Focus();
		else if (Active == PanelView.Output) OutputView.RequestFocus();
	}

	public int ErrorCount { get; private set; }

	public int WarningCount { get; private set; }

	public int InfoCount { get; private set; }

	public Func<string?>? ActiveFile { get; set; }

	public Func<IEnumerable<string>>? OpenFiles { get; set; }

	public string? WorkspaceRoot { get; set; }

	public void SetProblems(string owner, bool build, IReadOnlyList<Diagnostic> diagnostics) {
		if (diagnostics.Count == 0) _problemsByOwner.Remove(owner);
		else {
			Problem[] converted = new Problem[diagnostics.Count];

			for (int i = 0; i < converted.Length; i++) {
				Diagnostic diagnostic = diagnostics[i];
				converted[i] = new Problem(diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Message, diagnostic.Severity, diagnostic.Code, build);
			}

			_problemsByOwner[owner] = converted;
		}

		int total = 0;

		foreach (Problem[] entries in _problemsByOwner.Values) total += entries.Length;

		Problem[] all = new Problem[total];
		int errors = 0, warnings = 0, infos = 0, at = 0;

		foreach (Problem[] entries in _problemsByOwner.Values) {
			foreach (Problem problem in entries) {
				all[at++] = problem;

				switch (problem.Severity) {
					case DiagnosticSeverity.Error: errors++; break;
					case DiagnosticSeverity.Warning: warnings++; break;
					default: infos++; break;
				}
			}
		}

		_problems = all;
		ErrorCount = errors;
		WarningCount = warnings;
		InfoCount = infos;
		ProblemsView.Sync();
	}

	public void SetReferences(string title, IReadOnlyList<HostLocation> locations) {
		ReferencesTitle = title;
		_references = [.. locations];
		ReferencesView.Sync();

		if (_references.Length == 0) {
			if (Active == PanelView.References) Show(PanelView.Problems);
			else SyncView();

			return;
		}

		Show(PanelView.References);
	}

	public void Show(PanelView view) {
		IsOpen = true;
		Active = view;
		ActiveContainer = null;

		if (view == PanelView.Terminal) EnsureTerminal();

		SyncView();
	}

	public void Toggle(PanelView view) {
		if (IsOpen && Active == view && ActiveContainer is null) {
			IsOpen = false;
			return;
		}

		Show(view);
	}

	public void Resize(float height, float available) => Height = Math.Clamp(height, 80f, Math.Max(80f, available));

	private List<PanelView> GroupOf(PanelView view) => _groups.Find(group => group.Contains(view)) ?? [view];

	public IReadOnlyList<string> SaveGroups() {
		List<string> saved = [];

		foreach (List<PanelView> group in _groups) {
			if (group.Count > 1) saved.Add(string.Join(',', group.Select(view => FormattableString.Invariant($"{view}:{_weights[(int)view]:0.###}"))));
		}

		return saved;
	}

	public void RestoreGroups(IReadOnlyList<string> saved) {
		foreach (string entry in saved) {
			List<PanelView> group = [];

			foreach (Range range in entry.AsSpan().Split(',')) {
				ReadOnlySpan<char> part = entry.AsSpan()[range];
				int colon = part.IndexOf(':');
				ReadOnlySpan<char> name = colon < 0 ? part : part[..colon];

				if (!Enum.TryParse(name, out PanelView view) || group.Contains(view)) continue;

				group.Add(view);

				if (colon >= 0 && float.TryParse(part[(colon + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float weight) && weight > 0f) _weights[(int)view] = weight;
			}

			if (group.Count < 2) continue;

			foreach (PanelView view in group) GroupOf(view).Remove(view);

			_groups.RemoveAll(static existing => existing.Count == 0);
			_groups.Add(group);
		}

		OrderTabs();
		SyncView();
	}

	private void PreviewDrop(PanelView view, float x, float y) {
		(PixelRect? highlight, int index, bool split) = DropTarget(view, x, y);

		_dropIndex = index;
		_dropSplit = split;

		if (Nullable.Equals(highlight, _dropHighlight)) return;

		_dropHighlight = highlight;
		Repaint();
	}

	private (PixelRect? Highlight, int Index, bool Split) DropTarget(PanelView view, float x, float y) {
		(float titleX, float titleY) = _title.GetAbsPos();
		List<PanelView> source = GroupOf(view);

		if (y >= titleY && y < titleY + _title.Tall && x >= titleX && x < titleX + _title.Wide) {
			int slot = TabSlotAt(x, out float markerX);
			float marker = 2f * Scale;

			return (PixelRect.Around(markerX - (marker / 2f), titleY + (4f * Scale), marker, _title.Tall - (8f * Scale)), slot, true);
		}

		if (!IsWithin(x, y) || ActiveContainer is not null) return (null, -1, false);

		List<PanelView> target = GroupOf(Active);

		for (int i = 0; i < target.Count; i++) {
			Panel pane = _views[(int)target[i]];
			(float paneX, float paneY) = pane.GetAbsPos();
			float top = target.Count > 1 ? _headers[(int)target[i]].GetAbsPos().Y : paneY;
			float height = paneY + pane.Tall - top;

			if (x < paneX || x >= paneX + pane.Wide) continue;

			if (target.Count == 1 && target[0] == view) return (null, -1, false);

			bool right = x >= paneX + (pane.Wide / 2f);
			float half = pane.Wide / 2f;
			int index = right ? i + 1 : i;

			return (PixelRect.Around(right ? paneX + half : paneX, top, half, height), index, false);
		}

		return (null, -1, false);
	}

	private int TabSlotAt(float x, out float markerX) {
		markerX = _title.GetAbsPos().X;
		int slot = 0;

		for (int i = 0; i < _groups.Count; i++) {
			PanelTab tab = _tabs[(int)_groups[i][0]];

			if (!tab.Visible) {
				if (x >= markerX) slot = i + 1;

				continue;
			}

			(float tabX, _) = tab.GetAbsPos();

			if (x < tabX + (tab.Wide / 2f)) {
				markerX = tabX;
				return i;
			}

			markerX = tabX + tab.Wide;
			slot = i + 1;
		}

		return slot;
	}

	private void OrderTabs() {
		foreach (PanelTab tab in _tabs) _title.RemoveChild(tab);

		foreach (List<PanelView> group in _groups) {
			foreach (PanelView view in group) _title.AddChild(_tabs[(int)view]);
		}

		foreach ((_, PanelTab tab) in _containers) {
			_title.RemoveChild(tab);
			_title.AddChild(tab);
		}
	}

	private void DropView(PanelView view, float x, float y) {
		(PixelRect? highlight, int index, bool split) = DropTarget(view, x, y);

		_dropHighlight = null;
		_dropIndex = -1;
		Repaint();

		if (highlight is null) return;

		List<PanelView> source = GroupOf(view);

		if (split) {
			int position = _groups.IndexOf(source);

			if (source.Count > 1) source.Remove(view);
			else {
				_groups.RemoveAt(position);

				if (position < index) index--;
			}

			_groups.Insert(Math.Clamp(index, 0, _groups.Count), source.Count == 0 || source.Contains(view) ? source : [view]);
			Active = view;
			OrderTabs();
			SyncView();
			return;
		}

		List<PanelView> target = GroupOf(Active);
		int current = target.IndexOf(view);

		if (current >= 0) {
			target.RemoveAt(current);

			if (current < index) index--;
		}
		else {
			source.Remove(view);
			_groups.RemoveAll(static existing => existing.Count == 0);
		}

		target.Insert(Math.Clamp(index, 0, target.Count), view);
		_weights[(int)view] = target.Count > 1 ? target.Where(other => other != view).Average(other => _weights[(int)other]) : 1f;
		Active = view;
		SyncView();
	}

	private void SyncView() {
		_tabs[(int)PanelView.References].Visible = _references.Length > 0;

		bool builtIn = ActiveContainer is null;
		List<PanelView> activeGroup = GroupOf(Active);
		bool merged = builtIn && activeGroup.Count > 1;

		for (int i = 0; i < _tabs.Length; i++) {
			List<PanelView> group = GroupOf((PanelView)i);
			_tabs[i].Visible = group[0] == (PanelView)i && ((PanelView)i != PanelView.References || _references.Length > 0);
			_tabs[i].Selected = builtIn && ReferenceEquals(group, activeGroup) && group[0] == (PanelView)i;
		}

		for (int i = 0; i < _views.Length; i++) {
			bool shown = builtIn && activeGroup.Contains((PanelView)i);
			_views[i].Visible = shown;
			_views[i].Dock = shown && merged ? DockType.None : DockType.Fill;
			_headers[i].Visible = shown && merged;
		}

		foreach ((ViewPaneContainer container, PanelTab tab) in _containers) {
			bool active = container.Container.Id == ActiveContainer;
			tab.Selected = active;
			container.Visible = active;
		}

		bool terminalMerged = merged && activeGroup.Contains(PanelView.Terminal);
		bool terminalTitle = builtIn && !merged && Active == PanelView.Terminal;
		Panel? terminalHost = terminalTitle ? _title : terminalMerged ? _headers[(int)PanelView.Terminal] : null;

		foreach (ActionButton action in (ReadOnlySpan<ActionButton>)[_profiles, _newTerminal, _splitTerminal, _killTerminal]) {
			if (!ReferenceEquals(action.Parent, terminalHost)) {
				action.Parent?.RemoveChild(action);
				terminalHost?.AddChild(action);
			}

			action.Dock = ReferenceEquals(terminalHost, _title) ? DockType.Right : DockType.None;
			action.Visible = terminalHost is not null;
		}
		_channel.Visible = builtIn && Active == PanelView.Output;

		bool consoleMerged = merged && activeGroup.Contains(PanelView.DebugConsole);
		bool consoleAlone = builtIn && !merged && Active == PanelView.DebugConsole;
		_clearOutput.Visible = builtIn && !merged && Active is PanelView.Output or PanelView.DebugConsole;

		if (consoleAlone) PlaceFilter(_title);
		else if (!consoleMerged) PlaceFilter(null);

		_channel.SetText($"{ActiveChannel.GetName()} $(chevron-down)");
		InvalidateLayout();
		Repaint();
	}

	#region Output channels

	public void ActivateChannel(int index) {
		IReadOnlyList<IOutputChannel> all = Channels.All();

		if (index < 0 || index >= all.Count) return;

		ActiveChannel = all[index];
		OutputView.ResetScroll();
		SyncView();
	}

	public IOutputChannel Channel(string name) {
		IOutputChannel channel = Channels.Get(name);
		channel.OnAppended(OnChannelAppended);
		return channel;
	}

	private void OnChannelAppended(IOutputChannel channel) {
		if (channel != ActiveChannel) return;

		_dirty = true;
		Changed?.Invoke();
	}

	private void OpenChannelMenu() {
		Menu menu = new();
		IReadOnlyList<IOutputChannel> all = Channels.All();

		for (int i = 0; i < all.Count; i++) {
			int index = i;
			menu.Add(new MenuItem(all[i].GetName(), action: () => ActivateChannel(index)) { Checked = all[i] == ActiveChannel });
		}

		(float x, float y) = _channel.GetAbsPos();
		menu.Open(x, y + _channel.Tall);
	}

	public string CopyOutput() => OutputView.Copy();

	public void SelectAllOutput() => OutputView.SelectAll();

	public void ClearOutputSelection() => OutputView.ClearSelection();

	#endregion

	#region Terminals

	public TerminalSession? Terminal => _activeTerminal >= 0 && _activeTerminal < _terminals.Count ? _terminals[_activeTerminal] : null;

	public IReadOnlyList<TerminalSession> Terminals => _terminals;

	public int ActiveTerminal => _activeTerminal;

	public IReadOnlyList<TerminalProfile> Profiles => _profileList.Count > 0 ? _profileList : _profileList = TerminalProfiles.Detect();

	private void OpenProfileMenu() {
		Menu menu = new();

		foreach (TerminalProfile profile in Profiles) menu.Add(new MenuItem(profile.Name, action: () => NewTerminal(profile)));

		(float x, float y) = _profiles.GetAbsPos();
		menu.Open(x, y + _profiles.Tall);
	}

	private void EnsureTerminal() {
		if (_terminals.Count == 0) NewTerminal(TerminalProfiles.Default(Profiles, DefaultProfile));
	}

	public void CopyActiveGroup(List<TerminalSession> destination) {
		destination.Clear();

		if ((uint)_activeTerminal >= (uint)_terminals.Count) return;

		int group = _terminalGroups[_activeTerminal];

		for (int i = 0; i < _terminals.Count; i++) {
			if (_terminalGroups[i] == group) destination.Add(_terminals[i]);
		}
	}

	public bool IsSplitMember(int index) => index > 0 && index < _terminals.Count && _terminalGroups[index] == _terminalGroups[index - 1];

	public void ActivateSession(TerminalSession session) {
		int index = _terminals.IndexOf(session);

		if (index < 0 || index == _activeTerminal) return;

		_activeTerminal = index;
		TerminalView.Sync();
		Repaint();
	}

	public void NewTerminal(TerminalProfile profile, bool split = false) {
		TerminalSession session = new() { History = History };
		session.Changed += MarkDirty;

		if (!session.Start(profile, WorkingDirectory, 80, 24)) {
			Output.Append($"Failed to start {profile.Name} ({profile.Path}).");
			session.Dispose();
			return;
		}

		Output.Append($"Started {profile.Name}.");

		if (split && (uint)_activeTerminal < (uint)_terminals.Count) {
			int group = _terminalGroups[_activeTerminal];
			int insert = _activeTerminal + 1;

			while (insert < _terminals.Count && _terminalGroups[insert] == group) insert++;

			_terminals.Insert(insert, session);
			_terminalGroups.Insert(insert, group);
			_activeTerminal = insert;
		}
		else {
			_terminals.Add(session);
			_terminalGroups.Add(_nextTerminalGroup++);
			_activeTerminal = _terminals.Count - 1;
		}

		TerminalView.Sync();
		FocusActive();
	}

	public void RunTask(string name, string commandLine, string? workingDirectory, Action<string>? lines = null, Action<int>? finished = null, bool dedicated = true) {
		int reuse = dedicated ? IndexOfTask(name) : -1;

		if (reuse >= 0) {
			_taskNames.Remove(_terminals[reuse]);
			_terminals[reuse].Dispose();
			_terminals.RemoveAt(reuse);
			_terminalGroups.RemoveAt(reuse);
		}

		TerminalSession session = new() { History = History, LineObserver = lines, ExitObserver = finished };
		session.Changed += MarkDirty;

		TerminalProfile task = new(name, Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/c {commandLine}");

		if (!session.Start(task, workingDirectory ?? WorkingDirectory, 120, 30)) {
			Output.Append($"Failed to run {commandLine}.");
			session.Dispose();
			return;
		}

		_taskNames[session] = name;

		if (reuse >= 0) {
			_terminals.Insert(reuse, session);
			_terminalGroups.Insert(reuse, _nextTerminalGroup++);
		}
		else {
			_terminals.Add(session);
			_terminalGroups.Add(_nextTerminalGroup++);
		}

		_activeTerminal = reuse >= 0 ? reuse : _terminals.Count - 1;
		Show(PanelView.Terminal);
		TerminalView.Sync();
		_dirty = true;
	}

	private int IndexOfTask(string name) {
		for (int i = 0; i < _terminals.Count; i++)
			if (_taskNames.TryGetValue(_terminals[i], out string? label) && string.Equals(label, name, StringComparison.Ordinal)) return i;

		return -1;
	}

	public void ActivateTerminal(int index) {
		if (index < 0 || index >= _terminals.Count) return;

		_activeTerminal = index;
		TerminalView.Sync();
	}

	public void CloseTerminal(int index) {
		if (index < 0 || index >= _terminals.Count) return;

		_taskNames.Remove(_terminals[index]);
		_terminals[index].Dispose();
		_terminals.RemoveAt(index);
		_terminalGroups.RemoveAt(index);

		_activeTerminal = Math.Min(_activeTerminal, _terminals.Count - 1);
		TerminalView.Sync();

		if (_terminals.Count == 0 && Active == PanelView.Terminal) IsOpen = false;
	}

	public void MarkDirty() {
		_dirty = true;

		long now = Stopwatch.GetTimestamp();
		long previous = Interlocked.Read(ref _lastNotified);

		if (Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds < 15d) return;

		if (Interlocked.CompareExchange(ref _lastNotified, now, previous) != previous) return;

		Changed?.Invoke();
	}

	public bool IsDirty => _dirty;

	public bool TakeDirty() {
		bool dirty = _dirty;
		_dirty = false;
		return dirty;
	}

	#endregion

	public override void PerformLayout() {
		float scale = Scale;
		float title = TitleHeight * scale;
		float action = ActionSize * scale;
		float select = ChannelSelectHeight * scale;

		_title.SetSize(Wide, title);
		_title.DockPadding = new Thickness(Metrics.Space4 * scale, 0f, Metrics.Space8 * scale, 0f);

		foreach (Panel child in _title.Children) {
			switch (child) {
				case ActionButton button:
					button.SetSize(action, action);
					button.DockMargin = new Thickness(0f, (title - action) / 2f, 0f, (title - action) / 2f);
					break;

				case PanelTab tab:
					tab.SetSize(tab.PreferredWidth, title);
					break;
			}
		}

		TextEntry filter = DebugConsole.Filter;

		if (ReferenceEquals(filter.Parent, _title)) {
			float height = DebugConsoleView.FilterControlHeight * scale;
			float available = Wide * 0.3f;
			filter.Dock = DockType.Right;
			filter.SetSize(Math.Clamp(available, TitleFilterMinWidth * scale, TitleFilterMaxWidth * scale), height);
			filter.DockMargin = new Thickness(0f, (title - height) / 2f, TitleFilterMargin * scale, (title - height) / 2f);
		}
		_channel.SetSize(ChannelSelectWidth * scale, select);
		_channel.DockMargin = new Thickness(0f, (title - select) / 2f, Metrics.Space8 * scale, (title - select) / 2f);

		base.PerformLayout();
		LayoutGroup(scale, title);

	}

	private void LayoutGroup(float scale, float title) {
		List<PanelView> group = ActiveContainer is null ? GroupOf(Active) : [];
		int sashes = Math.Max(0, group.Count - 1);

		while (_sashes.Count < sashes) {
			int boundary = _sashes.Count;
			Splitter sash = new(vertical: true) { Name = "PanelSash" };
			sash.Dragged = (x, _) => ResizePanes(boundary, x);
			_sashes.Add(sash);
			AddChild(sash);
		}

		for (int i = 0; i < _sashes.Count; i++) _sashes[i].Visible = i < sashes;

		if (group.Count < 2) return;

		float header = PanelPaneHeader.Height * scale;
		float total = 0f;

		foreach (PanelView view in group) total += _weights[(int)view];

		float top = title;
		float height = Math.Max(0f, Tall - top);
		float left = 0f;
		float sashWidth = Metrics.SplitterHitWidth * scale;

		for (int i = 0; i < group.Count; i++) {
			int view = (int)group[i];
			float right = i == group.Count - 1 ? Wide : MathF.Round(left + (Wide * _weights[view] / total));

			_headers[view].SetBounds(left, top, right - left, header);
			_views[view].Dock = DockType.None;
			_views[view].SetBounds(left, top + header, right - left - (i < group.Count - 1 ? scale : 0f), Math.Max(0f, height - header));

			if (i < group.Count - 1) _sashes[i].SetBounds(right - (sashWidth / 2f), top, sashWidth, height);

			if (group[i] == PanelView.DebugConsole) LayoutConsoleHeader(_headers[view], scale);
			else if (group[i] == PanelView.Terminal) LayoutHeaderActions(_headers[view], scale, _killTerminal, _splitTerminal, _profiles, _newTerminal);

			left = right;
		}
	}

	private void PlaceFilter(Panel? host) {
		TextEntry filter = DebugConsole.Filter;

		if (ReferenceEquals(filter.Parent, host)) return;

		filter.Parent?.RemoveChild(filter);
		DebugConsole.FilterInBody = ReferenceEquals(host, DebugConsole);
		filter.Dock = ReferenceEquals(host, _title) ? DockType.Right : DockType.None;

		if (host is not null) host.AddChild(filter);

		filter.Visible = host is not null;
	}

	private static void LayoutHeaderActions(PanelPaneHeader header, float scale, params ReadOnlySpan<ActionButton> actions) {
		float size = HeaderActionSize * scale;
		float gap = Metrics.Space4 * scale;
		float right = header.Wide - gap;
		float middle = (header.Tall - size) / 2f;

		foreach (ActionButton action in actions) {
			action.SetBounds(right - size, middle, size, size);
			right -= size + gap;
		}
	}

	private void LayoutConsoleHeader(PanelPaneHeader header, float scale) {
		float action = HeaderActionSize * scale;
		float gap = Metrics.Space4 * scale;
		float right = header.Wide - gap;
		float middle = (header.Tall - action) / 2f;

		_consoleClear.SetBounds(right - action, middle, action, action);
		right -= action + gap;
		_consoleSearch.SetBounds(right - action, middle, action, action);
		right -= action + gap;

		Panel pane = _views[(int)PanelView.DebugConsole];
		bool inHeader = !(pane.Wide < FilterInHeaderMinimumWidth * scale && pane.Tall > FilterInHeaderMaximumHeight * scale);

		PlaceFilter(inHeader ? header : DebugConsole);

		if (!inHeader) return;

		float height = DebugConsoleView.FilterControlHeight * scale;
		float width = Math.Min(PaneFilterWidth * scale, Math.Max(0f, right - (header.Wide * 0.4f)));
		DebugConsole.Filter.SetBounds(right - width, (header.Tall - height) / 2f, width, height);
	}

	private void ResizePanes(int boundary, float screenX) {
		List<PanelView> group = GroupOf(Active);

		if (boundary + 1 >= group.Count) return;

		Panel leftPane = _views[(int)group[boundary]];
		Panel rightPane = _views[(int)group[boundary + 1]];
		float start = leftPane.GetAbsPos().X;
		float pair = (rightPane.GetAbsPos().X + rightPane.Wide) - start;
		float minimum = Math.Min(MinimumPaneWidth * Scale, pair / 2f);
		float leftWidth = Math.Clamp(screenX - start, minimum, pair - minimum);
		float weights = _weights[(int)group[boundary]] + _weights[(int)group[boundary + 1]];

		_weights[(int)group[boundary]] = weights * leftWidth / pair;
		_weights[(int)group[boundary + 1]] = weights * (pair - leftWidth) / pair;
		InvalidateLayout();
		Repaint();
	}

	private sealed class TitleStrip : Panel
	{
		public override void PerformLayout() {
			base.PerformLayout();

			float limit = Wide;

			foreach (Panel child in Children) {
				if (child.Visible && child.Dock == DockType.Right) limit = MathF.Min(limit, child.X);
			}

			foreach (Panel child in Children) {
				if (child is PanelTab tab && tab.Visible && tab.X + tab.Wide > limit) tab.SetBounds(tab.X, tab.Y, MathF.Max(0f, limit - tab.X), tab.Tall);
			}
		}
	}

	public override void PaintBackground(IPainter painter) {
		float radius = Metrics.RadiusLarge * Scale;
		painter.PushRoundedClip(ScreenX, ScreenY, Wide, Tall, radius);
		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.PanelBackground);
	}

	public override void PostChildPaint(IPainter painter) {
		if (_dropHighlight is { } rect) {
			float scale = Scale;
			painter.FillRoundedRectangle(rect.Left, rect.Top, rect.Width, rect.Height, Metrics.RadiusLarge * scale, Theme.Accent.WithAlpha(0.14f));
			painter.StrokeRoundedRectangle(rect.Left + scale, rect.Top + scale, rect.Width - (2f * scale), rect.Height - (2f * scale), Metrics.RadiusLarge * scale, 2f * scale, Theme.Accent.WithAlpha(0.8f));
		}

		painter.PopRoundedClip();
	}

	public override void PaintBorder(IPainter painter) => painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Metrics.StrokeThickness * Scale, Theme.PanelBorder);

	public void Dispose() {
		foreach (TerminalSession session in _terminals) session.Dispose();

		_terminals.Clear();
		_activeTerminal = -1;
	}
}
