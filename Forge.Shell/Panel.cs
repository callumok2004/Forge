using System.Diagnostics;

using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Terminal;
using Forge.Theming;

namespace Forge.Shell;

public enum PanelView
{
	Problems,
	Output,
	DebugConsole,
	Terminal,

	References,
}

public readonly record struct Problem(string File, int Line, int Column, string Message, bool IsError);

public sealed class Panel : IDisposable
{
	private const float TitleHeight = 32f;
	private const float RowHeight = 20f;
	private const float PaddingX = 12f;

	private const float ActionSize = 22f;

	private const float TerminalListWidth = 150f;

	private const float ChannelSelectWidth = 180f;

	private readonly string[] _lineBuffer = new string[256];

	private Problem[] _problems = [];

	private readonly List<TerminalSession> _terminals = [];

	private IReadOnlyList<TerminalProfile> _profiles = [];
	private int _activeTerminal = -1;
	private float _scrollY;
	private int _hoverView = -1;
	private PanelAction _hoverAction;
	private int _hoverProfile = -1;
	private int _hoverTerminal = -1;
	private bool _hoverTerminalClose;
	private int _hoverChannel = -1;

	private int _selectionAnchor = -1;
	private int _selectionFocus = -1;

	private int _outputFirstLine;
	private float _outputTop;
	private float _outputLineHeight = 1f;
	private int _outputVisible = 1;

	private float MaxOutputScroll() =>
		Math.Max(0f, (ActiveChannel.GetCount() - _outputVisible) * _outputLineHeight);

	private float _titleTop;
	private float _left;
	private float _width;

	private volatile bool _dirty;
	private long _lastNotified;
	private readonly Dictionary<TerminalSession, string> _taskNames = [];

	public bool IsOpen { get; set; }

	public PanelView Active { get; private set; } = PanelView.Terminal;

	public float Height { get; set; } = 240f;

	public IOutputChannel Output { get; }

	public IOutputChannel ActiveChannel { get; private set; }

	public bool IsChannelMenuOpen { get; private set; }

	public void ToggleChannelMenu() => IsChannelMenuOpen = !IsChannelMenuOpen;

	public void CloseChannelMenu() => IsChannelMenuOpen = false;

	public void ActivateChannel(int index) {
		IReadOnlyList<IOutputChannel> all = Channels.All();

		if (index >= 0 && index < all.Count) {
			ActiveChannel = all[index];
			ClearOutputSelection();
			_scrollY = 0f;
		}

		IsChannelMenuOpen = false;
	}

	public Panel() {
		Output = Channels.Get("Forge");
		ActiveChannel = Output;

		Channels.OnAdded(() => {
			_dirty = true;
			Changed?.Invoke();
		});
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

	public TerminalHistory? History { get; set; }

	public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

	public event Action? Changed;

	private void NotifyCoalesced() {
		long now = Stopwatch.GetTimestamp();
		long previous = Interlocked.Read(ref _lastNotified);

		if (Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds < 15d) return;

		if (Interlocked.CompareExchange(ref _lastNotified, now, previous) != previous) return;

		Changed?.Invoke();
	}

	public IReadOnlyList<Problem> Problems => _problems;

	public void SetProblems(IEnumerable<Problem> problems) => _problems = [.. problems];

	public void Show(PanelView view) {
		IsOpen = true;
		Active = view;
		_scrollY = 0f;

		if (view == PanelView.Terminal) EnsureTerminal();
	}

	public void Toggle(PanelView view) {
		if (IsOpen && Active == view) {
			IsOpen = false;
			return;
		}

		Show(view);
	}

	public void Resize(float height, float available) =>
		Height = Math.Clamp(height, 80f, Math.Max(80f, available));

	#region Terminal

	public TerminalSession? Terminal =>
		_activeTerminal >= 0 && _activeTerminal < _terminals.Count ? _terminals[_activeTerminal] : null;

	public IReadOnlyList<TerminalSession> Terminals => _terminals;

	public int ActiveTerminal => _activeTerminal;

	public IReadOnlyList<TerminalProfile> Profiles => _profiles.Count > 0 ? _profiles : _profiles = TerminalProfiles.Detect();

	public string? DefaultProfile { get; set; }

	public bool IsProfileMenuOpen { get; private set; }

	public void ToggleProfileMenu() => IsProfileMenuOpen = !IsProfileMenuOpen;

	public void CloseProfileMenu() => IsProfileMenuOpen = false;

	private void EnsureTerminal() {
		if (_terminals.Count == 0) NewTerminal(TerminalProfiles.Default(Profiles, DefaultProfile));
	}

	public void NewTerminal(TerminalProfile profile) {
		TerminalSession session = new() { History = History };

		session.Changed += () => {
			_dirty = true;
			NotifyCoalesced();
		};

		if (!session.Start(profile, WorkingDirectory, 80, 24)) {
			Output.Append($"Failed to start {profile.Name} ({profile.Path}).");
			session.Dispose();
			return;
		}

		Output.Append($"Started {profile.Name}.");

		_terminals.Add(session);
		_activeTerminal = _terminals.Count - 1;
		IsProfileMenuOpen = false;
	}

	public void RunTask(string name, string commandLine, string? workingDirectory,
		Action<string>? lines = null, Action<int>? finished = null, bool dedicated = true) {
		int reuse = dedicated ? IndexOfTask(name) : -1;

		if (reuse >= 0) {
			_terminals[reuse].Dispose();
			_terminals.RemoveAt(reuse);
		}

		TerminalSession session = new() { History = History, LineObserver = lines, ExitObserver = finished };

		session.Changed += () => {
			_dirty = true;
			NotifyCoalesced();
		};

		TerminalProfile task = new(name, Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
			$"/c {commandLine}");

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
		IsProfileMenuOpen = false;
		_dirty = true;
	}

	private int IndexOfTask(string name) {
		for (int i = 0; i < _terminals.Count; i++) {
			if (_taskNames.TryGetValue(_terminals[i], out string? label) &&
				string.Equals(label, name, StringComparison.Ordinal)) return i;
		}

		return -1;
	}

	public void ActivateTerminal(int index) {
		if (index >= 0 && index < _terminals.Count) _activeTerminal = index;
	}

	public void CloseTerminal(int index) {
		if (index < 0 || index >= _terminals.Count) return;

		_taskNames.Remove(_terminals[index]);
		_terminals[index].Dispose();
		_terminals.RemoveAt(index);

		_activeTerminal = Math.Min(_activeTerminal, _terminals.Count - 1);

		if (_terminals.Count == 0 && Active == PanelView.Terminal) IsOpen = false;
	}

	public void MarkDirty() {
		_dirty = true;
		NotifyCoalesced();
	}

	public bool IsDirty => _dirty;

	public bool TakeDirty() {
		bool dirty = _dirty;
		_dirty = false;
		return dirty;
	}

	#endregion

	#region Output selection

	public void ClearOutputSelection() => _selectionAnchor = _selectionFocus = -1;

	public int OutputLineAt(float x, float y) {
		if (!IsOpen || Active != PanelView.Output || y < _outputTop || x < _left || x >= _left + _width) return -1;

		int line = _outputFirstLine + (int)((y - _outputTop) / _outputLineHeight);
		int total = ActiveChannel.GetCount();

		return line >= 0 && line < total ? line : -1;
	}

	public bool BeginOutputSelection(float x, float y) {
		int line = OutputLineAt(x, y);

		if (line < 0) {
			ClearOutputSelection();
			return false;
		}

		_selectionAnchor = _selectionFocus = line;
		return true;
	}

	public void ExtendOutputSelection(float x, float y) {
		if (_selectionAnchor < 0) return;

		int line = OutputLineAt(x, y);

		if (line >= 0) _selectionFocus = line;
	}

	public string CopyOutput() {
		if (_selectionAnchor < 0 || _selectionFocus < 0) return ActiveChannel.ReadAll();

		return ActiveChannel.ReadRange(Math.Min(_selectionAnchor, _selectionFocus),
			Math.Max(_selectionAnchor, _selectionFocus));
	}

	public void SelectAllOutput() {
		int total = ActiveChannel.GetCount();

		if (total > 0) {
			_selectionAnchor = 0;
			_selectionFocus = total - 1;
		}
	}

	#endregion

	#region Hit testing

	public int ViewAt(IPainter painter, float x, float y, float scale) {
		if (!IsOpen || y < _titleTop || y >= _titleTop + (TitleHeight * scale)) return -1;

		float cursor = _left + (PaddingX * scale);

		for (int i = 0; i < ViewNames.Length; i++) {
			float width = painter.GetUi().Measure(ViewNames[i]) + (Metrics.Space16 * scale);

			if (x >= cursor && x < cursor + width) return i;

			cursor += width;
		}

		return -1;
	}

	public enum PanelAction
	{
		None,
		Close,
		ProfileMenu,
		NewTerminal,

		ChannelMenu,

		ClearOutput,
	}

	public PanelAction ActionAt(float x, float y, float scale) {
		if (!IsOpen || y < _titleTop || y >= _titleTop + (TitleHeight * scale)) return PanelAction.None;

		float size = ActionSize * scale;
		float right = _left + _width - (Metrics.Space8 * scale);
		float fromRight = right - x;

		if (fromRight < 0f) return PanelAction.None;

		if (fromRight <= size) return PanelAction.Close;

		if (Active == PanelView.Output) {
			if (fromRight <= size * 2f) return PanelAction.ClearOutput;

			float selectRight = (size * 2f) + (Metrics.Space8 * scale);

			return fromRight >= selectRight && fromRight <= selectRight + (ChannelSelectWidth * scale)
				? PanelAction.ChannelMenu
				: PanelAction.None;
		}

		if (Active != PanelView.Terminal) return PanelAction.None;

		if (fromRight <= size * 2f) return PanelAction.ProfileMenu;

		return fromRight <= size * 3f ? PanelAction.NewTerminal : PanelAction.None;
	}

	public int ProfileAt(IPainter painter, float x, float y, float scale) {
		if (!IsProfileMenuOpen) return -1;

		float rowHeight = 22f * scale;
		float width = ProfileMenuWidth(painter, scale);
		float height = (Profiles.Count * rowHeight) + (Metrics.Space4 * 2f * scale);
		float left = _left + _width - (Metrics.Space8 * scale) - width;
		float top = _titleTop + (TitleHeight * scale);

		if (x < left || x >= left + width || y < top || y >= top + height) return -1;

		int row = (int)((y - top - (Metrics.Space4 * scale)) / rowHeight);
		return row >= 0 && row < Profiles.Count ? row : -1;
	}

	private float ProfileMenuWidth(IPainter painter, float scale) {
		float widest = 0f;

		foreach (TerminalProfile profile in Profiles) widest = Math.Max(widest, painter.GetUi().Measure(profile.Name));

		return widest + (Metrics.Space16 * 2f * scale);
	}

	public void SetHover(int view, PanelAction action) {
		_hoverView = view;
		_hoverAction = action;
	}

	public void Activate(int view) {
		if (view >= 0 && view < ViewNames.Length) Show((PanelView)view);
	}

	public void ScrollByWheel(int wheelDelta, float scale) {
		if (Active == PanelView.Terminal) {
			Terminal?.Screen.ScrollBy((wheelDelta / 120) * 3);
			return;
		}

		float lines = (wheelDelta / 120f) * 3f;

		if (Active == PanelView.Output) {
			_scrollY = Math.Clamp(_scrollY + (lines * _outputLineHeight), 0f, MaxOutputScroll());
			return;
		}

		_scrollY = Math.Max(0f, _scrollY - (lines * RowHeight * scale));
	}

	#endregion

	private static readonly string[] BaseViewNames = ["Problems", "Output", "Debug Console", "Terminal"];

	private static readonly string[] WithReferences =
		[.. BaseViewNames, "References"];

	private string[] ViewNames => _references.Length > 0 ? WithReferences : BaseViewNames;

	private HostLocation[] _references = [];

	private string _referencesTitle = "References";

	private int _hoverReference = -1;

	public void SetReferences(string title, IReadOnlyList<HostLocation> locations) {
		_referencesTitle = title;
		_references = [.. locations];
		_scrollY = 0f;

		if (_references.Length == 0) {
			if (Active == PanelView.References) Active = PanelView.Problems;

			return;
		}

		Show(PanelView.References);
	}

	public int ReferenceAt(float y, float scale) {
		if (!IsOpen || Active != PanelView.References) return -1;

		float rowHeight = RowHeight * scale;
		float top = _titleTop + (TitleHeight * scale);
		int index = (int)((y - top + _scrollY) / rowHeight);

		return index >= 0 && index < _references.Length ? index : -1;
	}

	public HostLocation? Reference(int index) =>
		index >= 0 && index < _references.Length ? _references[index] : null;

	public void SetReferenceHover(int index) => _hoverReference = index;

	public void Render(IPainter painter, float x, float y, float width, float height, float scale) {
		_left = x;
		_width = width;
		_titleTop = y;

		float radius = Metrics.RadiusLarge * scale;

		painter.PushRoundedClip(x, y, width, height, radius);
		painter.FillRoundedRectangle(x, y, width, height, radius, Theme.ChromeBackground);

		DrawTitle(painter, x, y, width, scale);

		float contentTop = y + (TitleHeight * scale);
		float contentHeight = Math.Max(0f, height - (TitleHeight * scale));

		painter.PushClip(x, contentTop, width, contentHeight);

		switch (Active) {
			case PanelView.Problems:
				DrawProblems(painter, x, contentTop, width, contentHeight, scale);
				break;

			case PanelView.Output:
				DrawOutput(painter, x, contentTop, width, contentHeight, scale);
				break;

			case PanelView.DebugConsole:
				DrawEmpty(painter, x, contentTop, width, contentHeight,
					"No debug session is running.", scale);
				break;

			case PanelView.Terminal:
				DrawTerminal(painter, x, contentTop, width, contentHeight, scale);
				break;

			case PanelView.References:
				DrawReferences(painter, x, contentTop, width, contentHeight, scale);
				break;
		}

		painter.PopClip();
		painter.PopRoundedClip();

		painter.StrokeRoundedRectangle(x, y, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.PanelBorder);
	}

	private void DrawTitle(IPainter painter, float x, float y, float width, float scale) {
		ITextFont font = painter.GetUi();
		float height = TitleHeight * scale;
		float centreY = y + (height / 2f);
		float baseline = centreY - (font.GetLineHeight() / 2f) + font.GetBaseline();

		float cursor = x + (PaddingX * scale);

		for (int i = 0; i < ViewNames.Length; i++) {
			bool active = (int)Active == i;
			float itemWidth = font.Measure(ViewNames[i]) + (Metrics.Space16 * scale);

			if (_hoverView == i && !active) {
				painter.FillRoundedRectangle(cursor, centreY - (11f * scale), itemWidth, 22f * scale,
					Metrics.RadiusSmall * scale, Theme.ListHoverBackground);
			}

			painter.DrawText(font, ViewNames[i], cursor + (Metrics.Space8 * scale), baseline,
				active ? Theme.TabActiveForeground : Theme.TabInactiveForeground);

			if (active) {
				painter.FillRectangle(cursor + (Metrics.Space8 * scale), y + height - (2f * scale),
					itemWidth - (Metrics.Space16 * scale), 2f * scale, Theme.TabActiveBorderTop);
			}

			cursor += itemWidth;
		}

		float size = ActionSize * scale;
		float right = x + width - (Metrics.Space8 * scale);

		DrawAction(painter, Codicon.Close, PanelAction.Close, right - (size / 2f), centreY, size, scale);

		if (Active == PanelView.Output) {
			DrawAction(painter, Codicon.ClearAll, PanelAction.ClearOutput,
				right - size - (size / 2f), centreY, size, scale);

			DrawChannelSelect(painter, right - (size * 2f) - (Metrics.Space8 * scale) -
				(ChannelSelectWidth * scale), centreY, scale);
		}

		if (Active == PanelView.Terminal) {
			DrawAction(painter, Codicon.ChevronDown, PanelAction.ProfileMenu,
				right - size - (size / 2f), centreY, size, scale);

			DrawAction(painter, Codicon.Add, PanelAction.NewTerminal,
				right - (size * 2f) - (size / 2f), centreY, size, scale);
		}
	}

	private void DrawAction(IPainter painter, Codicon icon, PanelAction action, float centreX,
		float centreY, float size, float scale) {
		if (_hoverAction == action) {
			painter.FillRoundedRectangle(centreX - (size / 2f), centreY - (size / 2f), size, size,
				Metrics.RadiusSmall * scale, Theme.ListHoverBackground);
		}

		painter.DrawIcon(icon, centreX, centreY, Metrics.IconSize * scale, Theme.ChromeForeground);
	}

	private void DrawChannelSelect(IPainter painter, float left, float centreY, float scale) {
		ITextFont font = painter.GetUi();
		float height = 20f * scale;
		float width = ChannelSelectWidth * scale;
		float top = centreY - (height / 2f);
		float radius = Metrics.RadiusSmall * scale;

		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.CommandCentreBackground);
		painter.StrokeRoundedRectangle(left, top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		if (_hoverAction == PanelAction.ChannelMenu) {
			painter.FillRoundedRectangle(left, top, width, height, radius, Theme.HoverOverlay);
		}

		float chevron = Metrics.Space16 * scale;
		float textLeft = left + (Metrics.Space8 * scale);
		float baseline = centreY - (font.GetLineHeight() / 2f) + font.GetBaseline();

		painter.PushClip(textLeft, top, width - (Metrics.Space8 * scale) - chevron, height);
		painter.DrawText(font, ActiveChannel.GetName(), textLeft, baseline, Theme.EditorForeground);
		painter.PopClip();

		painter.DrawIcon(Codicon.ChevronDown, left + width - (chevron / 2f) - (Metrics.Space4 * scale),
			centreY, Metrics.IconSize * scale, Theme.ChromeForeground);
	}

	private (float Left, float Top, float Width, float Height) ChannelMenuBounds(float scale) {
		float size = ActionSize * scale;
		float width = ChannelSelectWidth * scale;
		float left = _left + _width - (Metrics.Space8 * scale) - (size * 2f) - (Metrics.Space8 * scale) - width;
		float rowHeight = ActionSize * scale;
		float padding = Metrics.Space4 * scale;
		float height = (Channels.All().Count * rowHeight) + (padding * 2f);

		return (left, _titleTop + (TitleHeight * scale), width, height);
	}

	public void RenderChannelMenu(IPainter painter, float scale) {
		if (!IsChannelMenuOpen) return;

		IReadOnlyList<IOutputChannel> all = Channels.All();
		ITextFont font = painter.GetUi();
		float rowHeight = ActionSize * scale;
		float padding = Metrics.Space4 * scale;
		(float left, float top, float width, float height) = ChannelMenuBounds(scale);
		float radius = Metrics.RadiusLarge * scale;

		painter.PushRoundedClip(left, top, width, height, radius);
		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.WidgetBackground);

		for (int i = 0; i < all.Count; i++) {
			float rowTop = top + padding + (i * rowHeight);

			if (i == _hoverChannel) {
				painter.FillRoundedRectangle(left + padding, rowTop, width - (padding * 2f), rowHeight,
					Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);
			}

			float baseline = rowTop + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

			if (all[i] == ActiveChannel) {
				painter.DrawIcon(Codicon.Check, left + (Metrics.Space12 * scale), rowTop + (rowHeight / 2f),
					Metrics.IconSize * scale, Theme.EditorForeground);
			}

			painter.DrawText(font, all[i].GetName(), left + (Metrics.SpaceXl * scale), baseline,
				Theme.EditorForeground);
		}

		painter.PopRoundedClip();

		painter.StrokeRoundedRectangle(left, top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}

	public int ChannelAt(float x, float y, float scale) {
		if (!IsChannelMenuOpen) return -1;

		(float left, float top, float width, float height) = ChannelMenuBounds(scale);

		if (x < left || x >= left + width || y < top || y >= top + height) return -1;

		int row = (int)((y - top - (Metrics.Space4 * scale)) / (ActionSize * scale));
		return row >= 0 && row < Channels.All().Count ? row : -1;
	}

	public void SetChannelHover(int index) => _hoverChannel = index;

	public void RenderProfileMenu(IPainter painter, float scale) {
		if (!IsProfileMenuOpen) return;

		ITextFont font = painter.GetUi();
		float rowHeight = ActionSize * scale;
		float padding = Metrics.Space4 * scale;
		float width = ProfileMenuWidth(painter, scale);
		float height = (Profiles.Count * rowHeight) + (padding * 2f);

		float left = _left + _width - (Metrics.Space8 * scale) - width;
		float top = _titleTop + (TitleHeight * scale);
		float radius = Metrics.RadiusLarge * scale;

		painter.PushRoundedClip(left, top, width, height, radius);
		painter.FillRoundedRectangle(left, top, width, height, radius, Theme.WidgetBackground);

		for (int i = 0; i < Profiles.Count; i++) {
			float rowTop = top + padding + (i * rowHeight);

			if (i == _hoverProfile) {
				painter.FillRoundedRectangle(left + padding, rowTop, width - (padding * 2f), rowHeight,
					Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);
			}

			float baseline = rowTop + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

			painter.DrawText(font, Profiles[i].Name, left + (Metrics.Space16 * scale), baseline,
				Theme.EditorForeground);
		}

		painter.PopRoundedClip();

		painter.StrokeRoundedRectangle(left, top, width, height, radius,
			Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}

	public void SetProfileHover(int index) => _hoverProfile = index;

	private void DrawProblems(IPainter painter, float x, float y, float width, float height, float scale) {
		Problem[] problems = _problems;

		if (problems.Length == 0) {
			DrawEmpty(painter, x, y, width, height,
				"No problems have been detected in the workspace.", scale);
			return;
		}

		ITextFont font = painter.GetUi();
		float rowHeight = RowHeight * scale;
		int first = Math.Max(0, (int)(_scrollY / rowHeight));

		for (int i = first; i < problems.Length; i++) {
			float rowTop = y + ((i - first) * rowHeight) - (_scrollY % rowHeight);

			if (rowTop > y + height) break;

			Problem problem = problems[i];
			float baseline = rowTop + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float cursor = x + (PaddingX * scale);

			painter.DrawIcon(problem.IsError ? Codicon.Error : Codicon.Warning,
				cursor + (Metrics.Space8 * scale), rowTop + (rowHeight / 2f), Metrics.IconSize * scale,
				problem.IsError ? Theme.ErrorForeground : Theme.WarningForeground);

			cursor += Metrics.Space16 * scale + (Metrics.Space8 * scale);

			painter.DrawText(font, problem.Message, cursor, baseline, Theme.EditorForeground);
			cursor += font.Measure(problem.Message) + (Metrics.Space12 * scale);

			painter.DrawText(font, $"{problem.File}:{problem.Line}", cursor, baseline,
				Theme.SectionHeaderForeground);
		}
	}

	private void DrawReferences(IPainter painter, float x, float y, float width, float height, float scale) {
		HostLocation[] locations = _references;

		if (locations.Length == 0) {
			DrawEmpty(painter, x, y, width, height, $"No results for {_referencesTitle}.", scale);
			return;
		}

		ITextFont font = painter.GetUi();
		float rowHeight = RowHeight * scale;
		int first = Math.Max(0, (int)(_scrollY / rowHeight));

		for (int i = first; i < locations.Length; i++) {
			float rowTop = y + ((i - first) * rowHeight) - (_scrollY % rowHeight);

			if (rowTop > y + height) break;

			HostLocation location = locations[i];

			if (i == _hoverReference) painter.FillRectangle(x, rowTop, width, rowHeight, Theme.ListHoverBackground);

			float baseline = rowTop + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float cursor = x + (PaddingX * scale);

			string where = $"{Path.GetFileName(location.Path)}:{location.Line}";

			painter.DrawText(font, where, cursor, baseline, Theme.SectionHeaderForeground);
			cursor += font.Measure(where) + (Metrics.Space12 * scale);

			painter.DrawText(font, location.Preview, cursor, baseline, Theme.EditorForeground);
		}
	}

	private void DrawOutput(IPainter painter, float x, float y, float width, float height, float scale) {
		ITextFont font = painter.GetCode();
		float rowHeight = font.GetLineHeight();
		int visible = Math.Max(1, (int)(height / rowHeight));
		IOutputChannel channel = ActiveChannel;
		int total = channel.GetCount();

		_outputTop = y;
		_outputLineHeight = rowHeight;
		_outputVisible = visible;

		if (total == 0) {
			_outputFirstLine = 0;
			DrawEmpty(painter, x, y, width, height,
				$"No output yet from {channel.GetName()}.", scale);
			return;
		}

		int maxSkip = Math.Max(0, total - visible);
		_scrollY = Math.Min(_scrollY, maxSkip * rowHeight);
		int skip = Math.Clamp(maxSkip - (int)Math.Round(_scrollY / rowHeight), 0, maxSkip);
		int count = channel.CopyTo(_lineBuffer, skip);

		_outputFirstLine = skip;

		int first = Math.Min(_selectionAnchor, _selectionFocus);
		int last = Math.Max(_selectionAnchor, _selectionFocus);

		for (int i = 0; i < count && i <= visible; i++) {
			float rowTop = y + (i * rowHeight);
			int line = skip + i;

			if (_selectionAnchor >= 0 && line >= first && line <= last) {
				painter.FillRectangle(x, rowTop, width, rowHeight, Theme.SelectionBackground);
			}

			painter.DrawText(font, _lineBuffer[i], x + (PaddingX * scale), rowTop + font.GetBaseline(),
				Theme.EditorForeground);
		}
	}

	private void DrawTerminal(IPainter painter, float x, float y, float width, float height, float scale) {
		if (Terminal is not { } session) {
			DrawEmpty(painter, x, y, width, height, "The terminal could not be started.", scale);
			return;
		}

		if (_terminals.Count > 1) {
			float listWidth = TerminalListWidth * scale;
			width = Math.Max(0f, width - listWidth);
			DrawTerminalList(painter, x + width, y, listWidth, height, scale);
		}

		ITextFont font = painter.GetCode();
		float padding = Metrics.Space8 * scale;

		float cellWidth = font.GetCellWidth();
		float rowHeight = font.GetLineHeight();

		int columns = Math.Max(1, (int)((width - (padding * 2f)) / cellWidth));
		int rows = Math.Max(1, (int)((height - padding) / rowHeight));

		session.Resize(columns, rows);

		session.Drain();

		TerminalScreen screen = session.Screen;

		lock (screen.Sync) {
			for (int row = 0; row < rows; row++) {
				ReadOnlySpan<TerminalCell> cells = screen.Row(row);

				if (cells.IsEmpty) continue;

				float top = y + (row * rowHeight);
				DrawTerminalRow(painter, cells, x + padding, top, cellWidth, rowHeight, font);
			}

			if (screen.CursorVisible && screen.ScrollOffset == 0) {
				float caretX = x + padding + (screen.CursorColumn * cellWidth);
				float caretY = y + (screen.CursorRow * rowHeight);

				ReadOnlySpan<char> ghost = session.SuggestionRemainder;

				if (!ghost.IsEmpty) {
					int room = Math.Max(0, screen.Columns - screen.CursorColumn);

					painter.DrawText(font, ghost[..Math.Min(ghost.Length, room)], caretX,
						caretY + font.GetBaseline(), Theme.SectionHeaderForeground);
				}

				painter.FillRectangle(caretX, caretY, cellWidth, rowHeight,
					Theme.CursorForeground.WithAlpha(0.6f));
			}
		}
	}

	private static void DrawTerminalRow(IPainter painter, ReadOnlySpan<TerminalCell> cells, float left,
		float top, float cellWidth, float rowHeight, ITextFont font) {
		Span<char> run = stackalloc char[512];
		int column = 0;

		while (column < cells.Length) {
			int start = column;
			int foreground = cells[start].Foreground;
			int background = cells[start].Background;
			int length = 0;

			while (column < cells.Length && length < run.Length &&
				cells[column].Foreground == foreground && cells[column].Background == background) {
				run[length++] = cells[column].Character;
				column++;
			}

			float runLeft = left + (start * cellWidth);

			if (background != 0) {
				painter.FillRectangle(runLeft, top, length * cellWidth, rowHeight, Convert(background));
			}

			painter.DrawText(font, run[..length], runLeft, top + font.GetBaseline(),
				foreground == 0 ? Theme.EditorForeground : Convert(foreground));
		}
	}

	private void DrawTerminalList(IPainter painter, float x, float y, float width, float height, float scale) {
		ITextFont font = painter.GetUi();
		float rowHeight = Metrics.TreeRowHeight * scale;

		painter.DrawVerticalLine(x, y, y + height, Theme.PanelBorder);

		for (int i = 0; i < _terminals.Count; i++) {
			float top = y + (i * rowHeight);

			if (top + rowHeight > y + height) break;

			bool active = i == _activeTerminal;
			bool hovered = i == _hoverTerminal;
			float inset = Metrics.Space4 * scale;

			if (active || hovered) {
				painter.FillRoundedRectangle(x + inset, top, width - (inset * 2f), rowHeight,
					Metrics.RadiusSmall * scale,
					active ? Theme.ListActiveSelectionBackground : Theme.ListHoverBackground);
			}

			float baseline = top + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float centreY = top + (rowHeight / 2f);
			Color colour = active ? Theme.TabActiveForeground : Theme.ChromeForeground;

			painter.DrawIcon(Codicon.Terminal, x + (Metrics.Space12 * scale), centreY,
				Metrics.IconSize * scale, colour);

			float closeSize = Metrics.IconSize * scale;
			float closeCentre = x + width - (Metrics.Space8 * scale) - (closeSize / 2f);
			bool showClose = active || hovered;

			painter.PushClip(x, top,
				width - (Metrics.Space16 * scale) - (showClose ? closeSize : 0f), rowHeight);

			painter.DrawText(font, _terminals[i].Title,
				x + (Metrics.Space16 * scale) + (Metrics.Space4 * scale), baseline, colour);

			painter.PopClip();

			if (showClose) {
				painter.DrawIcon(Codicon.Close, closeCentre, centreY, closeSize,
					_hoverTerminalClose && hovered ? Theme.TabActiveForeground : Theme.SectionHeaderForeground);
			}
		}
	}

	public int TerminalAt(float x, float y, float scale) {
		if (Active != PanelView.Terminal || _terminals.Count < 2) return -1;

		float listLeft = _left + _width - (TerminalListWidth * scale);
		float contentTop = _titleTop + (TitleHeight * scale);

		if (x < listLeft || y < contentTop) return -1;

		int row = (int)((y - contentTop) / (Metrics.TreeRowHeight * scale));
		return row >= 0 && row < _terminals.Count ? row : -1;
	}

	public bool IsOverTerminalClose(float x, float y, float scale) {
		if (TerminalAt(x, y, scale) < 0) return false;

		float size = Metrics.IconSize * scale;
		float right = _left + _width - (Metrics.Space8 * scale);
		return x >= right - size && x < right;
	}

	public void SetTerminalHover(int row, bool close) {
		_hoverTerminal = row;
		_hoverTerminalClose = close;
	}

	private static Color Convert(int argb) => Color.FromBytes(
		(byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF),
		(byte)((argb >> 24) & 0xFF));

	private static void DrawEmpty(IPainter painter, float x, float y, float width, float height,
		ReadOnlySpan<char> message, float scale) {
		ITextFont font = painter.GetUi();
		painter.DrawText(font, message, x + (PaddingX * scale),
			y + (Metrics.Space12 * scale) + font.GetBaseline(), Theme.SectionHeaderForeground);
	}

	public void Dispose() {
		foreach (TerminalSession session in _terminals) session.Dispose();

		_terminals.Clear();
		_activeTerminal = -1;
	}
}
