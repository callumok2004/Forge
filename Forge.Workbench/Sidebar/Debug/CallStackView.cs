using System.Collections.Concurrent;

using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class CallStackView : DebugTreeView
{
	private readonly ConcurrentDictionary<int, IReadOnlyList<DebugStackFrame>> _stacks = [];
	private readonly HashSet<int> _requested = [];
	private readonly HashSet<int> _expandedThreads = [];
	private readonly HashSet<int> _collapsedThreads = [];
	private readonly HashSet<string> _shownGroups = [];
	private DebugState _state = DebugState.Inactive;

	public CallStackView() : base("DebugCallStack") { }

	public IDebug? Debugger {
		get;
		set {
			field = value;
			value?.OnModelChanged(Refresh);
			value?.OnStateChanged(StateChanged);
			Refresh();
		}
	}

	private void StateChanged() {
		DebugState state = Debugger?.GetState() ?? DebugState.Inactive;

		if (state != _state) {
			_stacks.Clear();
			_requested.Clear();
			_shownGroups.Clear();
		}

		_state = state;
		Refresh();
	}

	public void CollapseAll() {
		_expandedThreads.Clear();

		foreach (DebugThread thread in Debugger?.GetThreads() ?? []) _collapsedThreads.Add(thread.Id);

		Refresh();
	}

	protected override void Build(List<DebugRow> rows) {
		if (Debugger is not { } debugger || debugger.GetState() == DebugState.Inactive) return;

		IReadOnlyList<DebugThread> threads = debugger.GetThreads();
		int focusedThread = debugger.GetFocusedThread() ?? -1;
		int focusedFrame = debugger.GetFocusedFrame()?.Id ?? -1;

		if (threads.Count <= 1) {
			AddFrames(rows, debugger, focusedThread, debugger.GetCallStack(), focusedFrame, 0);
			return;
		}

		foreach (DebugThread thread in threads) {
			int id = thread.Id;
			bool focused = id == focusedThread;
			bool expanded = focused ? !_collapsedThreads.Contains(id) : _expandedThreads.Contains(id);
			IReadOnlyList<DebugStackFrame>? frames = focused ? debugger.GetCallStack() : expanded ? Stack(debugger, id) : null;

			rows.Add(new DebugRow {
				Depth = 0,
				Label = thread.Name.Length > 0 ? thread.Name : $"Thread {id}",
				Separator = null,
				Bold = focused,
				Badge = State(thread, debugger.GetState()),
				Expandable = true,
				Expanded = expanded,
				Toggle = () => ToggleThread(id, focused),
				Context = menu => menu.Add(new MenuItem("Copy Call Stack", null, frames is null ? null : () => Clipboard.SetText(Describe(frames)))),
			});

			if (!expanded) continue;

			if (frames is null) rows.Add(new DebugRow { Depth = 1, Label = "Loading...", LabelColor = Theme.DescriptionForeground, Separator = null });
			else AddFrames(rows, debugger, id, frames, focused ? focusedFrame : -1, 1);
		}
	}

	private static string State(DebugThread thread, DebugState session) {
		if (!thread.Stopped && session != DebugState.Stopped) return "RUNNING";

		return thread.StopReason is { Length: > 0 } reason ? $"PAUSED ON {reason.ToUpperInvariant()}" : "PAUSED";
	}

	private void ToggleThread(int id, bool focused) {
		if (focused) {
			if (!_collapsedThreads.Remove(id)) _collapsedThreads.Add(id);
		}
		else if (!_expandedThreads.Remove(id)) _expandedThreads.Add(id);

		Refresh();
	}

	private IReadOnlyList<DebugStackFrame>? Stack(IDebug debugger, int thread) {
		if (_stacks.TryGetValue(thread, out IReadOnlyList<DebugStackFrame>? frames)) return frames;

		if (_requested.Add(thread)) _ = debugger.ThreadStackAsync(thread).ContinueWith(task => {
			_stacks[thread] = task.IsCompletedSuccessfully ? task.Result : [];
			Refresh();
		}, TaskScheduler.Default);

		return null;
	}

	private static bool External(DebugStackFrame frame) => frame.PresentationHint == "subtle" || frame.SourcePath is not { Length: > 0 };

	private void AddFrames(List<DebugRow> rows, IDebug debugger, int thread, IReadOnlyList<DebugStackFrame> frames, int focusedFrame, int depth) {
		int i = 0;

		while (i < frames.Count) {
			int run = i;

			while (run < frames.Count && External(frames[run]) && frames[run].Id != focusedFrame) run++;

			int external = run - i;
			string group = $"{thread}:{i}";

			if (external >= 2 && !_shownGroups.Contains(group)) {
				rows.Add(new DebugRow {
					Depth = depth,
					Label = $"Show {external} more frames",
					LabelColor = Theme.TextLinkForeground,
					Separator = null,
					Activate = () => {
						_shownGroups.Add(group);
						Refresh();
					},
				});

				i = run;
				continue;
			}

			int end = Math.Max(run, i + 1);

			for (; i < end; i++) AddFrame(rows, debugger, thread, frames, i, focusedFrame, depth);
		}
	}

	private static void AddFrame(List<DebugRow> rows, IDebug debugger, int thread, IReadOnlyList<DebugStackFrame> frames, int index, int focusedFrame, int depth) {
		DebugStackFrame frame = frames[index];
		bool focused = frame.Id == focusedFrame;
		bool top = index == 0;
		int id = frame.Id;

		rows.Add(new DebugRow {
			Depth = depth,
			Label = frame.Name,
			Separator = null,
			Detail = frame.SourcePath is { Length: > 0 } path ? $"{Path.GetFileName(path)}:{frame.Line}" : null,
			Icon = top || focused ? (top ? Codicon.DebugStackframe : Codicon.DebugStackframeActive) : default,
			IconColor = top ? Theme.DebugStackframeForeground : Theme.DebugStackframeFocusedForeground,
			Dimmed = External(frame),
			Focused = focused,
			Activate = () => _ = debugger.FocusThreadFrameAsync(thread, id),
			Context = menu => {
				menu.Add(new MenuItem("Copy Frame", null, () => Clipboard.SetText(Describe([frame]))));
				menu.Add(new MenuItem("Copy Call Stack", null, () => Clipboard.SetText(Describe(frames))));
			},
		});
	}

	private static string Describe(IReadOnlyList<DebugStackFrame> frames) => string.Join(Environment.NewLine, frames.Select(static frame => frame.SourcePath is { Length: > 0 } path ? $"{frame.Name} ({path}:{frame.Line})" : frame.Name));

	protected override void AddGeneralContext(Menu menu) {
		if (menu.Children.Count > 0) menu.AddSeparator();

		menu.Add(new MenuItem("Collapse All", null, CollapseAll));
	}
}
