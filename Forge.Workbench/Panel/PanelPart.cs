using System.Diagnostics;

using Forge.Common.Commands;
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

public readonly record struct Problem(string File, int Line, int Column, string Message, bool IsError);

public sealed class PanelPart : Panel, IDisposable
{
	private const float TitleHeight = 32f;
	private const float ActionSize = 22f;
	private const float ChannelSelectWidth = 180f;
	private const float ChannelSelectHeight = 20f;

	private static readonly string[] ViewTitles = ["Problems", "Output", "Debug Console", "Terminal", "References"];

	private readonly List<TerminalSession> _terminals = [];
	private readonly Dictionary<TerminalSession, string> _taskNames = [];
	private readonly Panel _title;
	private readonly PanelTab[] _tabs;
	private readonly Panel[] _views;
	private readonly ActionButton _newTerminal;
	private readonly ActionButton _profiles;
	private readonly ActionButton _clearOutput;
	private readonly Button _channel;

	private IReadOnlyList<TerminalProfile> _profileList = [];
	private Problem[] _problems = [];
	private HostLocation[] _references = [];
	private int _activeTerminal = -1;
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

		_title = new Panel { Name = "PanelTitle", Dock = DockType.Top, MouseInputEnabled = false };

		ActionButton close = TitleAction(Codicon.Close, "Close Panel");
		close.Command = "workbench.action.togglePanel";
		_profiles = TitleAction(Codicon.ChevronDown, "Launch Profile...");
		_profiles.Action = OpenProfileMenu;
		_newTerminal = TitleAction(Codicon.Add, "New Terminal");
		_newTerminal.Action = () => NewTerminal(TerminalProfiles.Default(Profiles, DefaultProfile));
		_clearOutput = TitleAction(Codicon.ClearAll, "Clear Output");
		_clearOutput.Action = () => {
			ActiveChannel.Clear();
			OutputView.ClearSelection();
		};

		_channel = new Button { Name = "Channel", Dock = DockType.Right, Foreground = Theme.EditorForeground, HoverBackground = Theme.HoverOverlay, Radius = Metrics.RadiusSmall, Padding = new Thickness(Metrics.Space8, 0f, Metrics.Space4, 0f), Action = OpenChannelMenu };
		_title.AddChild(_channel);

		_tabs = new PanelTab[ViewTitles.Length];

		for (int i = 0; i < ViewTitles.Length; i++) {
			PanelView view = (PanelView)i;
			_tabs[i] = new PanelTab(ViewTitles[i]) { Action = () => Show(view) };
			_title.AddChild(_tabs[i]);
		}

		AddChild(_title);

		_views = [ProblemsView, OutputView, new Label("No debug session is running.") { Name = "DebugConsole", Dock = DockType.Fill, Foreground = Theme.SectionHeaderForeground, Padding = new Thickness(Metrics.Space12, 0f, 0f, 0f) }, TerminalView, ReferencesView];

		foreach (Panel view in _views) AddChild(view);

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

	public bool IsOpen { get; set; }

	public PanelView Active { get; private set; } = PanelView.Terminal;

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

	public void SetProblems(IEnumerable<Problem> problems) {
		_problems = [.. problems];
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

		if (view == PanelView.Terminal) EnsureTerminal();

		SyncView();
	}

	public void Toggle(PanelView view) {
		if (IsOpen && Active == view) {
			IsOpen = false;
			return;
		}

		Show(view);
	}

	public void Resize(float height, float available) => Height = Math.Clamp(height, 80f, Math.Max(80f, available));

	private void SyncView() {
		_tabs[(int)PanelView.References].Visible = _references.Length > 0;

		for (int i = 0; i < _tabs.Length; i++) _tabs[i].Selected = i == (int)Active;

		for (int i = 0; i < _views.Length; i++) _views[i].Visible = i == (int)Active;

		_newTerminal.Visible = _profiles.Visible = Active == PanelView.Terminal;
		_clearOutput.Visible = _channel.Visible = Active == PanelView.Output;
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

	public void NewTerminal(TerminalProfile profile) {
		TerminalSession session = new() { History = History };
		session.Changed += MarkDirty;

		if (!session.Start(profile, WorkingDirectory, 80, 24)) {
			Output.Append($"Failed to start {profile.Name} ({profile.Path}).");
			session.Dispose();
			return;
		}

		Output.Append($"Started {profile.Name}.");

		_terminals.Add(session);
		_activeTerminal = _terminals.Count - 1;
		TerminalView.Sync();
		FocusActive();
	}

	public void RunTask(string name, string commandLine, string? workingDirectory, Action<string>? lines = null, Action<int>? finished = null, bool dedicated = true) {
		int reuse = dedicated ? IndexOfTask(name) : -1;

		if (reuse >= 0) {
			_taskNames.Remove(_terminals[reuse]);
			_terminals[reuse].Dispose();
			_terminals.RemoveAt(reuse);
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

		if (reuse >= 0) _terminals.Insert(reuse, session);
		else _terminals.Add(session);

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
		_title.DockPadding = new Thickness(Metrics.Space8 * scale, 0f, Metrics.Space8 * scale, 0f);

		foreach (Panel child in _title.Children) {
			switch (child) {
				case ActionButton button:
					button.SetSize(action, action);
					button.DockMargin = new Thickness(0f, (title - action) / 2f, 0f, (title - action) / 2f);
					break;

				case PanelTab tab:
					tab.FitContents();
					tab.SetSize(tab.Wide, title);
					break;
			}
		}

		_channel.SetSize(ChannelSelectWidth * scale, select);
		_channel.DockMargin = new Thickness(0f, (title - select) / 2f, Metrics.Space8 * scale, (title - select) / 2f);

		base.PerformLayout();
	}

	public override void PaintBackground(IPainter painter) {
		float radius = Metrics.RadiusLarge * Scale;
		painter.PushRoundedClip(ScreenX, ScreenY, Wide, Tall, radius);
		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.ChromeBackground);
	}

	public override void PostChildPaint(IPainter painter) => painter.PopRoundedClip();

	public override void PaintBorder(IPainter painter) => painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Metrics.StrokeThickness * Scale, Theme.PanelBorder);

	public void Dispose() {
		foreach (TerminalSession session in _terminals) session.Dispose();

		_terminals.Clear();
		_activeTerminal = -1;
	}
}
