using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core;
using Forge.Theming;

namespace Forge.Workbench;

public enum DebugSection
{
	Variables,
	Watch,
	CallStack,
	Breakpoints,
}

public sealed class DebugView : Panel
{
	private const float RowHeight = 22f;
	private const float IndentWidth = 14f;

	private readonly bool[] _collapsed = new bool[4];
	private readonly List<Row> _rows = [];
	private readonly Dictionary<int, IReadOnlyList<DebugVariable>> _variables = [];
	private readonly HashSet<int> _expanded = [];
	private readonly HashSet<int> _requested = [];

	private volatile bool _stale = true;
	private float _scrollY;
	private int _hover = -1;

	private readonly record struct Row(int Depth, string Label, string? Value, int Reference, bool IsHeader, Codicon Icon, bool Dimmed, bool Expanded, Action<DebugView>? Activate);

	public DebugView() {
		Name = "Debug";
		Dock = DockType.Fill;
	}

	public IDebug? Debugger {
		get;
		set {
			field = value;
			value?.OnModelChanged(MarkStale);
			value?.OnStateChanged(() => {
				if (value.GetState() != DebugState.Stopped) Reset();

				MarkStale();
			});
			MarkStale();
		}
	}

	public Func<int, Task<IReadOnlyList<DebugScope>>>? ScopesLoader { get; set; }

	public Func<int, Task<IReadOnlyList<DebugVariable>>>? VariablesLoader { get; set; }

	public Action? Changed { get; set; }

	public Action<string, int>? OpenBreakpoint { get; set; }

	public void Reset() {
		_variables.Clear();
		_expanded.Clear();
		_requested.Clear();
		MarkStale();
	}

	private void MarkStale() => _stale = true;

	private void Notify() {
		MarkStale();
		Changed?.Invoke();
	}

	#region Rows

	private void EnsureRows() {
		if (!_stale) return;

		_stale = false;
		_rows.Clear();

		if (Debugger is not { } debugger) return;

		AddHeader(DebugSection.Variables, "Variables");

		if (!_collapsed[(int)DebugSection.Variables]) BuildVariables(debugger);

		AddHeader(DebugSection.Watch, "Watch");

		if (!_collapsed[(int)DebugSection.Watch]) BuildWatches(debugger);

		AddHeader(DebugSection.CallStack, "Call Stack");

		if (!_collapsed[(int)DebugSection.CallStack]) BuildCallStack(debugger);

		AddHeader(DebugSection.Breakpoints, "Breakpoints");

		if (!_collapsed[(int)DebugSection.Breakpoints]) BuildBreakpoints(debugger);

		ClampScroll();
	}

	private void AddHeader(DebugSection section, string label) => _rows.Add(new Row(0, label, null, 0, true, default, false, !_collapsed[(int)section], view => view.ToggleSection(section)));

	private void AddMessage(string message) => _rows.Add(new Row(1, message, null, 0, false, default, true, false, null));

	private void BuildVariables(IDebug debugger) {
		if (debugger.GetState() != DebugState.Stopped) {
			AddMessage("Not paused");
			return;
		}

		if (debugger.GetFocusedFrame() is not { } frame) return;

		if (!_variables.TryGetValue(frame.Id, out IReadOnlyList<DebugVariable>? roots)) {
			LoadScopes(frame.Id);
			AddMessage("Loading...");
			return;
		}

		foreach (DebugVariable scope in roots) AddVariable(scope, 1);
	}

	private void AddVariable(DebugVariable variable, int depth) {
		int reference = variable.VariablesReference;
		bool expanded = reference > 0 && _expanded.Contains(reference);

		_rows.Add(new Row(depth, variable.Name, variable.Value, reference, false, default, false, expanded, reference > 0 ? view => view.ToggleVariable(reference) : null));

		if (!expanded || !_variables.TryGetValue(reference, out IReadOnlyList<DebugVariable>? children)) return;

		foreach (DebugVariable child in children) AddVariable(child, depth + 1);
	}

	private void BuildWatches(IDebug debugger) {
		IReadOnlyList<string> watches = debugger.GetWatchExpressions();

		if (watches.Count == 0) AddMessage("No expressions");

		foreach (string watch in watches) _rows.Add(new Row(1, watch, null, 0, false, default, false, false, null));
	}

	private void BuildCallStack(IDebug debugger) {
		IReadOnlyList<DebugStackFrame> frames = debugger.GetCallStack();

		if (frames.Count == 0) {
			AddMessage("Not paused");
			return;
		}

		int focused = debugger.GetFocusedFrame()?.Id ?? -1;

		foreach (DebugStackFrame frame in frames) {
			string location = frame.SourcePath is { Length: > 0 } path ? $"{Path.GetFileName(path)}:{frame.Line}" : string.Empty;
			int id = frame.Id;

			_rows.Add(new Row(1, frame.Name, location, id, false, default, id != focused && frame.PresentationHint == "subtle", false, view => view.Debugger?.FocusStackFrame(id)));
		}
	}

	private void BuildBreakpoints(IDebug debugger) {
		IReadOnlyList<SourceBreakpoint> breakpoints = debugger.GetBreakpoints();

		if (breakpoints.Count == 0) {
			AddMessage("No breakpoints");
			return;
		}

		foreach (SourceBreakpoint breakpoint in breakpoints) {
			Codicon icon = breakpoint.LogMessage is { Length: > 0 } ? Codicon.DebugBreakpointLog : breakpoint.Condition is { Length: > 0 } ? Codicon.DebugBreakpointConditional : Codicon.DebugBreakpoint;
			string path = breakpoint.Path;
			int line = breakpoint.EffectiveLine;

			_rows.Add(new Row(1, Path.GetFileName(path), line.ToString(), 0, false, icon, !breakpoint.Enabled || !debugger.AreBreakpointsActivated(), false, view => view.OpenBreakpoint?.Invoke(path, line)));
		}
	}

	private void ToggleSection(DebugSection section) {
		_collapsed[(int)section] = !_collapsed[(int)section];
		Notify();
	}

	private void ToggleVariable(int reference) {
		if (!_expanded.Add(reference)) {
			_expanded.Remove(reference);
			Notify();
			return;
		}

		Notify();

		if (!_requested.Add(reference) && _variables.ContainsKey(reference)) return;

		if (VariablesLoader is not { } loader) return;

		_ = loader(reference).ContinueWith(task => {
			if (task.IsCompletedSuccessfully) _variables[reference] = task.Result;

			Notify();
		}, TaskScheduler.Default);
	}

	private void LoadScopes(int frameId) {
		if (!_requested.Add(frameId) || ScopesLoader is not { } loader) return;

		_ = loader(frameId).ContinueWith(task => {
			if (!task.IsCompletedSuccessfully) return;

			List<DebugVariable> roots = [];

			foreach (DebugScope scope in task.Result) roots.Add(new DebugVariable(scope.Name, string.Empty, null, scope.VariablesReference, null, true, 0, 0));

			_variables[frameId] = roots;
			Notify();
		}, TaskScheduler.Default);
	}

	#endregion

	#region Input

	private void ClampScroll() => _scrollY = Math.Clamp(_scrollY, 0f, Math.Max(0f, (_rows.Count * RowHeight * Scale) - Tall));

	private int RowAt(float y) {
		int index = (int)((y + _scrollY) / (RowHeight * Scale));
		return index >= 0 && index < _rows.Count ? index : -1;
	}

	public override void OnCursorMoved(float x, float y) {
		EnsureRows();

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

	public override bool OnMousePressed(MouseButton button, float x, float y) => true;

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		EnsureRows();

		int row = RowAt(y);

		if (row >= 0 && _rows[row].Activate is { } activate) {
			activate(this);
			Repaint();
		}

		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float previous = _scrollY;
		_scrollY -= delta / 120f * 3f * RowHeight * Scale;
		ClampScroll();

		if (_scrollY != previous) Repaint();

		return true;
	}

	private void RepaintRow(int row) {
		if (row < 0) return;

		float height = RowHeight * Scale;
		Repaint(0f, (row * height) - _scrollY, Wide, height);
	}

	#endregion

	public override void Paint(IPainter painter) {
		EnsureRows();

		float scale = Scale;
		float rowHeight = RowHeight * scale;
		ITextFont font = painter.GetUi();
		ITextFont strong = painter.GetUiStrong();
		int first = Math.Max(0, (int)(_scrollY / rowHeight));

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		for (int i = first; i < _rows.Count; i++) {
			float top = ScreenY + (i * rowHeight) - _scrollY;

			if (top > ScreenY + Tall) break;

			if (!painter.NeedsPaint(ScreenX, top, Wide, rowHeight)) continue;

			Row row = _rows[i];
			float centreY = top + (rowHeight / 2f);
			float indent = ScreenX + (Metrics.Space8 * scale) + (row.Depth * IndentWidth * scale);
			ITextFont rowFont = row.IsHeader ? strong : font;
			float baseline = top + ((rowHeight - rowFont.GetLineHeight()) / 2f) + rowFont.GetBaseline();

			if (i == _hover && !row.IsHeader) painter.FillRectangle(ScreenX, top, Wide, rowHeight, Theme.ListHoverBackground);

			if (row.IsHeader) {
				painter.DrawChevron(indent + (5f * scale), centreY, 8f * scale, row.Expanded, Theme.SidebarTitleForeground);
				painter.DrawText(strong, row.Label, indent + (16f * scale), baseline, Theme.SidebarTitleForeground);
				continue;
			}

			float textX = indent;

			if (row.Icon != default) {
				painter.DrawIcon(row.Icon, indent + (6f * scale), centreY, 14f * scale, row.Dimmed ? Theme.DebugBreakpointDisabled : Theme.DebugBreakpointForeground);
				textX = indent + (16f * scale);
			}
			else if (row.Reference > 0) {
				painter.DrawChevron(indent + (5f * scale), centreY, 8f * scale, row.Expanded, Theme.SidebarFileForeground);
				textX = indent + (14f * scale);
			}

			painter.DrawText(rowFont, row.Label, textX, baseline, row.Dimmed ? Theme.SidebarTitleForeground : Theme.SidebarFileForeground);

			if (row.Value is { Length: > 0 } value) painter.DrawText(font, value, textX + rowFont.Measure(row.Label) + (Metrics.Space8 * scale), baseline, Theme.SidebarTitleForeground);
		}

		painter.PopClip();
	}
}
