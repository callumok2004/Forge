using System.Collections.Concurrent;

using Forge.Common.Debug;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class VariablesView : DebugTreeView
{
	private readonly ConcurrentDictionary<int, IReadOnlyList<DebugScope>> _scopes = [];
	private readonly HashSet<int> _requested = [];
	private readonly VariableTree _tree;
	private readonly HashSet<string> _seenScopes = [];
	private DebugState _state = DebugState.Inactive;

	public VariablesView() : base("DebugVariables") => _tree = new VariableTree(this, () => Debugger);

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

		if (state == DebugState.Stopped && _state != DebugState.Stopped) {
			_tree.NewStop();
			_scopes.Clear();
			_requested.Clear();
		}
		else if (state == DebugState.Inactive) {
			_tree.Clear();
			_scopes.Clear();
			_requested.Clear();
		}

		_state = state;
		Refresh();
	}

	public void CollapseAll() => _tree.CollapseAll();

	public void Reload() {
		_tree.NewStop();
		_scopes.Clear();
		_requested.Clear();
		Refresh();
	}

	protected override void Build(List<DebugRow> rows) {
		if (Debugger is not { } debugger || debugger.GetState() != DebugState.Stopped || debugger.GetFocusedFrame() is not { } frame) return;

		if (!_scopes.TryGetValue(frame.Id, out IReadOnlyList<DebugScope>? scopes)) {
			if (_requested.Add(frame.Id)) _ = debugger.ScopesAsync(frame.Id).ContinueWith(task => {
				_scopes[frame.Id] = task.IsCompletedSuccessfully ? task.Result : [];
				Refresh();
			}, TaskScheduler.Default);

			Message(rows, "Loading...");
			return;
		}

		for (int i = 0; i < scopes.Count; i++) {
			DebugScope scope = scopes[i];
			string path = $"scope:{scope.Name}";

			if (_seenScopes.Add(path) && i == 0 && !scope.Expensive) _tree.Expand(path);

			bool expanded = scope.VariablesReference > 0 && _tree.IsExpanded(path);

			rows.Add(new DebugRow {
				Depth = 0,
				Label = scope.Name,
				Bold = true,
				Separator = null,
				Expandable = scope.VariablesReference > 0,
				Expanded = expanded,
				Toggle = () => _tree.Toggle(path),
			});

			if (expanded) _tree.AddChildren(rows, scope.VariablesReference, path, 1, Context, Edit);
		}
	}

	private void Edit(DebugVariable variable, int container, string path) {
		if (Debugger is not { } debugger || !debugger.GetCapabilities().SetVariable) return;

		BeginEdit(path, variable.Value, text => _ = debugger.SetVariableAsync(container, variable.Name, text).ContinueWith(_ => {
			_tree.Invalidate(container);
			Refresh();
		}, TaskScheduler.Default));
	}

	private void Context(Menu menu, DebugVariable variable, int container, string path) {
		if (Debugger is not { } debugger) return;

		bool settable = !variable.ReadOnly && debugger.GetCapabilities().SetVariable;
		VariableTree.AddValueItems(menu, variable, debugger, settable ? () => Edit(variable, container, path) : null);
	}

	protected override void AddGeneralContext(Menu menu) {
		if (Debugger is not { } debugger) return;

		if (menu.Children.Count > 0) menu.AddSeparator();

		menu.Add(new MenuItem("Hex Display", null, () => {
			debugger.HexDisplay = !debugger.HexDisplay;
			Reload();
		}) { Checked = debugger.HexDisplay });
		menu.Add(new MenuItem("Collapse All", null, CollapseAll));
	}
}
