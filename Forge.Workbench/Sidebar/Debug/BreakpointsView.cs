using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class BreakpointsView : DebugTreeView
{
	public BreakpointsView() : base("DebugBreakpoints") { }

	public IDebug? Debugger {
		get;
		set {
			field = value;
			value?.OnModelChanged(Refresh);
			value?.OnStateChanged(Refresh);
			Refresh();
		}
	}

	public Action<string, int>? Open { get; set; }

	public Func<string?>? RootPath { get; set; }

	protected override void Build(List<DebugRow> rows) {
		if (Debugger is not { } debugger) return;

		bool activated = debugger.AreBreakpointsActivated();
		bool session = debugger.GetState() != DebugState.Inactive;

		foreach (ExceptionBreakpoint filter in debugger.GetExceptionBreakpoints()) {
			ExceptionBreakpoint current = filter;

			rows.Add(new DebugRow {
				Depth = 0,
				Label = filter.Label,
				Separator = null,
				Checked = filter.Enabled,
				Dimmed = !activated,
				CheckToggled = () => debugger.SetExceptionBreakpoint(current.Filter, !current.Enabled, current.Condition),
				Activate = () => debugger.SetExceptionBreakpoint(current.Filter, !current.Enabled, current.Condition),
			});
		}

		foreach (FunctionBreakpoint function in debugger.GetFunctionBreakpoints()) {
			string name = function.Name;

			rows.Add(new DebugRow {
				Depth = 0,
				Label = name,
				Separator = null,
				Icon = Codicon.DebugBreakpointFunction,
				IconColor = function.Enabled && activated ? Theme.DebugBreakpointForeground : Theme.DebugBreakpointDisabled,
				Dimmed = !function.Enabled || !activated,
				Actions = [new DebugRowAction(Codicon.Close, "Remove Breakpoint", () => debugger.RemoveFunctionBreakpoint(name))],
				Context = menu => menu.Add(new MenuItem("Remove Breakpoint", null, () => debugger.RemoveFunctionBreakpoint(name))),
			});
		}

		string? root = RootPath?.Invoke();

		foreach (SourceBreakpoint breakpoint in debugger.GetBreakpoints().OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.EffectiveLine)) {
			string path = breakpoint.Path;
			int line = breakpoint.EffectiveLine;
			bool enabled = breakpoint.Enabled;
			string display = root is { Length: > 0 } && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/') : path;
			string kind = breakpoint.LogMessage is { Length: > 0 } ? "Log" : breakpoint.Condition is { Length: > 0 } || breakpoint.HitCondition is { Length: > 0 } ? "Conditional" : string.Empty;

			rows.Add(new DebugRow {
				Depth = 0,
				Label = display,
				Separator = null,
				Detail = line.ToString(),
				Checked = enabled,
				Icon = Icon(breakpoint, session),
				IconColor = !enabled || !activated ? Theme.DebugBreakpointDisabled : breakpoint.LogMessage is { Length: > 0 } ? Theme.DebugBreakpointLogPoint : kind == "Conditional" ? Theme.DebugBreakpointConditional : Theme.DebugBreakpointForeground,
				Dimmed = !enabled || !activated,
				CheckToggled = () => debugger.EnableOrDisableBreakpoints(!enabled, path, line),
				Activate = () => Open?.Invoke(path, line),
				Actions = [new DebugRowAction(Codicon.Close, "Remove Breakpoint", () => debugger.RemoveBreakpoint(path, line))],
				Context = menu => {
					menu.Add(new MenuItem(enabled ? "Disable Breakpoint" : "Enable Breakpoint", null, () => debugger.EnableOrDisableBreakpoints(!enabled, path, line)));
					menu.Add(new MenuItem("Remove Breakpoint", null, () => debugger.RemoveBreakpoint(path, line)));
				},
			});
		}

		if (rows.Count == 0) Message(rows, "No breakpoints");
	}

	private static Codicon Icon(SourceBreakpoint breakpoint, bool session) {
		if (session && !breakpoint.Verified) return Codicon.DebugBreakpointUnverified;

		if (breakpoint.LogMessage is { Length: > 0 }) return Codicon.DebugBreakpointLog;

		return breakpoint.Condition is { Length: > 0 } || breakpoint.HitCondition is { Length: > 0 } ? Codicon.DebugBreakpointConditional : Codicon.DebugBreakpoint;
	}

	protected override void AddGeneralContext(Menu menu) {
		if (Debugger is not { } debugger) return;

		if (menu.Children.Count > 0) menu.AddSeparator();

		menu.Add(new MenuItem("Enable All Breakpoints", null, () => debugger.EnableOrDisableBreakpoints(true, null, 0)));
		menu.Add(new MenuItem("Disable All Breakpoints", null, () => debugger.EnableOrDisableBreakpoints(false, null, 0)));
		menu.Add(new MenuItem(debugger.AreBreakpointsActivated() ? "Deactivate Breakpoints" : "Activate Breakpoints", null, () => debugger.SetBreakpointsActivated(!debugger.AreBreakpointsActivated())));
		menu.AddSeparator();
		menu.Add(new MenuItem("Remove All Breakpoints", null, debugger.RemoveAllBreakpoints));
	}
}
