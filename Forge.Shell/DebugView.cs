using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Core;
using Forge.Theming;

namespace Forge.Shell;

public enum DebugSection
{
	Variables,
	Watch,
	CallStack,
	Breakpoints,
}

public sealed class DebugView
{
	private const float HeaderHeight = 24f;
	private const float RowHeight = 22f;
	private const float IndentWidth = 14f;

	private readonly bool[] _collapsed = new bool[4];
	private readonly List<Row> _rows = [];
	private readonly Dictionary<int, IReadOnlyList<DebugVariable>> _variables = [];
	private readonly HashSet<int> _expanded = [];
	private readonly HashSet<int> _requested = [];

	private float _scrollY;
	private float _height;
	private int _hoverRow = -1;

	public IDebug? Debugger { get; set; }

	public Func<int, Task<IReadOnlyList<DebugScope>>>? ScopesLoader { get; set; }

	public Func<int, Task<IReadOnlyList<DebugVariable>>>? VariablesLoader { get; set; }

	public Action? Changed { get; set; }

	private readonly record struct Row(DebugSection Section, int Depth, string Label, string? Value,
		int Reference, bool IsHeader, int Index, Codicon Icon, bool Dimmed);

	public void Reset() {
		_variables.Clear();
		_expanded.Clear();
		_requested.Clear();
	}

	public void Render(IPainter painter, float x, float y, float width, float height, float scale) {
		_height = height;

		Build(scale);

		float rowHeight = RowHeight * scale;
		ClampScroll(rowHeight);

		painter.PushClip(x, y, width, height);

		ITextFont font = painter.GetUi();
		ITextFont strong = painter.GetUiStrong();

		int first = Math.Max(0, (int)(_scrollY / rowHeight));
		int visible = (int)(height / rowHeight) + 2;

		for (int i = first; i < Math.Min(_rows.Count, first + visible); i++) {
			Row row = _rows[i];
			float top = y + (i * rowHeight) - _scrollY;

			if (top + rowHeight < y || top > y + height) continue;

			if (i == _hoverRow && !row.IsHeader) {
				painter.FillRectangle(x, top, width, rowHeight, Theme.ListHoverBackground);
			}

			float indent = x + (Metrics.Space8 * scale) + (row.Depth * IndentWidth * scale);
			ITextFont rowFont = row.IsHeader ? strong : font;
			float baseline = top + ((rowHeight - rowFont.GetLineHeight()) / 2f) + rowFont.GetBaseline();

			if (row.IsHeader) {
				painter.DrawChevron(indent + (5f * scale), top + (rowHeight / 2f), 8f * scale,
					!_collapsed[(int)row.Section], Theme.SidebarTitleForeground);

				painter.DrawText(strong, row.Label, indent + (16f * scale), baseline,
					Theme.SidebarTitleForeground);
				continue;
			}

			float textX = indent;

			if (row.Icon != default) {
				painter.DrawIcon(row.Icon, indent + (6f * scale), top + (rowHeight / 2f), 14f * scale,
					row.Dimmed ? Theme.DebugBreakpointDisabled : Theme.DebugBreakpointForeground);

				textX = indent + (16f * scale);
			}
			else if (row.Reference > 0) {
				painter.DrawChevron(indent + (5f * scale), top + (rowHeight / 2f), 8f * scale,
					_expanded.Contains(row.Reference), Theme.SidebarFileForeground);

				textX = indent + (14f * scale);
			}

			Color labelColor = row.Dimmed ? Theme.SidebarTitleForeground : Theme.SidebarFileForeground;
			painter.DrawText(rowFont, row.Label, textX, baseline, labelColor);

			if (row.Value is not { Length: > 0 } value) continue;

			float valueX = textX + rowFont.Measure(row.Label) + (Metrics.Space8 * scale);
			painter.DrawText(font, value, valueX, baseline, Theme.SidebarTitleForeground);
		}

		painter.PopClip();
	}

	public bool SetHover(float x, float y, float top, float scale) {
		int row = RowAt(y, top, scale);

		if (row == _hoverRow) return false;

		_hoverRow = row;
		return true;
	}

	public void Click(float y, float top, float scale) {
		int index = RowAt(y, top, scale);

		if (index < 0 || index >= _rows.Count) return;

		Row row = _rows[index];

		if (row.IsHeader) {
			_collapsed[(int)row.Section] = !_collapsed[(int)row.Section];
			Changed?.Invoke();
			return;
		}

		switch (row.Section) {
			case DebugSection.Variables when row.Reference > 0:
				Toggle(row.Reference);
				break;

			case DebugSection.CallStack:
				Debugger?.FocusStackFrame(row.Reference);
				break;

			case DebugSection.Breakpoints when Debugger is { } debugger:
				if (row.Index >= 0 && row.Index < debugger.GetBreakpoints().Count) {
					SourceBreakpoint breakpoint = debugger.GetBreakpoints()[row.Index];
					OpenBreakpoint?.Invoke(breakpoint.Path, breakpoint.EffectiveLine);
				}

				break;
		}

		Changed?.Invoke();
	}

	public Action<string, int>? OpenBreakpoint { get; set; }

	public void ScrollByWheel(int wheelDelta, float scale) {
		_scrollY -= (wheelDelta / 120f) * 3f * RowHeight * scale;
		ClampScroll(RowHeight * scale);
	}

	private void Toggle(int reference) {
		if (!_expanded.Add(reference)) {
			_expanded.Remove(reference);
			return;
		}

		if (!_requested.Add(reference) && _variables.ContainsKey(reference)) return;

		if (VariablesLoader is not { } loader) return;

		_ = loader(reference).ContinueWith(task => {
			if (task.IsCompletedSuccessfully) _variables[reference] = task.Result;

			Changed?.Invoke();
		}, TaskScheduler.Default);
	}

	private int RowAt(float y, float top, float scale) {
		float rowHeight = RowHeight * scale;
		int index = (int)((y - top + _scrollY) / rowHeight);

		return index >= 0 && index < _rows.Count ? index : -1;
	}

	private void ClampScroll(float rowHeight) {
		float maximum = Math.Max(0f, (_rows.Count * rowHeight) - _height);
		_scrollY = Math.Clamp(_scrollY, 0f, maximum);
	}

	private void Build(float scale) {
		_rows.Clear();

		if (Debugger is not { } debugger) return;

		AddHeader(DebugSection.Variables, "Variables");

		if (!_collapsed[(int)DebugSection.Variables]) BuildVariables(debugger);

		AddHeader(DebugSection.Watch, "Watch");

		if (!_collapsed[(int)DebugSection.Watch]) {
			IReadOnlyList<string> watches = debugger.GetWatchExpressions();

			if (watches.Count == 0) {
				_rows.Add(new Row(DebugSection.Watch, 1, "No expressions", null, 0, false, -1, default, true));
			}

			for (int i = 0; i < watches.Count; i++) {
				_rows.Add(new Row(DebugSection.Watch, 1, watches[i], null, 0, false, i, default, false));
			}
		}

		AddHeader(DebugSection.CallStack, "Call Stack");

		if (!_collapsed[(int)DebugSection.CallStack]) BuildCallStack(debugger);

		AddHeader(DebugSection.Breakpoints, "Breakpoints");

		if (!_collapsed[(int)DebugSection.Breakpoints]) BuildBreakpoints(debugger);
	}

	private void AddHeader(DebugSection section, string label) =>
		_rows.Add(new Row(section, 0, label, null, 0, true, -1, default, false));

	private void BuildVariables(IDebug debugger) {
		if (debugger.GetState() != DebugState.Stopped) {
			_rows.Add(new Row(DebugSection.Variables, 1, "Not paused", null, 0, false, -1, default, true));
			return;
		}

		if (debugger.GetFocusedFrame() is not { } frame) return;

		if (!_variables.TryGetValue(frame.Id, out IReadOnlyList<DebugVariable>? scopeRoots)) {
			LoadScopes(frame.Id);
			_rows.Add(new Row(DebugSection.Variables, 1, "Loading...", null, 0, false, -1, default, true));
			return;
		}

		foreach (DebugVariable scope in scopeRoots) AddVariable(scope, 1);
	}

	private void AddVariable(DebugVariable variable, int depth) {
		_rows.Add(new Row(DebugSection.Variables, depth, variable.Name, variable.Value,
			variable.VariablesReference, false, -1, default, false));

		if (variable.VariablesReference <= 0 || !_expanded.Contains(variable.VariablesReference)) return;

		if (!_variables.TryGetValue(variable.VariablesReference, out IReadOnlyList<DebugVariable>? children)) return;

		foreach (DebugVariable child in children) AddVariable(child, depth + 1);
	}

	private void LoadScopes(int frameId) {
		if (!_requested.Add(frameId) || ScopesLoader is not { } loader) return;

		_ = loader(frameId).ContinueWith(task => {
			if (!task.IsCompletedSuccessfully) return;

			List<DebugVariable> roots = [];

			foreach (DebugScope scope in task.Result) {
				roots.Add(new DebugVariable(scope.Name, string.Empty, null, scope.VariablesReference,
					null, true, 0, 0));
			}

			_variables[frameId] = roots;
			Changed?.Invoke();
		}, TaskScheduler.Default);
	}

	private void BuildCallStack(IDebug debugger) {
		IReadOnlyList<DebugStackFrame> frames = debugger.GetCallStack();

		if (frames.Count == 0) {
			_rows.Add(new Row(DebugSection.CallStack, 1, "Not paused", null, 0, false, -1, default, true));
			return;
		}

		int focused = debugger.GetFocusedFrame()?.Id ?? -1;

		foreach (DebugStackFrame frame in frames) {
			string location = frame.SourcePath is { Length: > 0 } path
				? $"{Path.GetFileName(path)}:{frame.Line}"
				: string.Empty;

			_rows.Add(new Row(DebugSection.CallStack, 1, frame.Name, location, frame.Id, false, -1,
				default, frame.Id != focused && frame.PresentationHint == "subtle"));
		}
	}

	private void BuildBreakpoints(IDebug debugger) {
		IReadOnlyList<SourceBreakpoint> breakpoints = debugger.GetBreakpoints();

		if (breakpoints.Count == 0) {
			_rows.Add(new Row(DebugSection.Breakpoints, 1, "No breakpoints", null, 0, false, -1,
				default, true));
			return;
		}

		for (int i = 0; i < breakpoints.Count; i++) {
			SourceBreakpoint breakpoint = breakpoints[i];

			Codicon icon = breakpoint.LogMessage is { Length: > 0 }
				? Codicon.DebugBreakpointLog
				: breakpoint.Condition is { Length: > 0 }
					? Codicon.DebugBreakpointConditional
					: Codicon.DebugBreakpoint;

			_rows.Add(new Row(DebugSection.Breakpoints, 1, Path.GetFileName(breakpoint.Path),
				$"{breakpoint.EffectiveLine}", 0, false, i, icon,
				!breakpoint.Enabled || !debugger.AreBreakpointsActivated()));
		}
	}
}
