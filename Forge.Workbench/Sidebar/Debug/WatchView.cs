using System.Collections.Concurrent;

using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class WatchView : DebugTreeView
{
	private const string NewKey = "watch:new";

	private readonly ConcurrentDictionary<string, DebugVariable?> _results = [];
	private readonly HashSet<string> _requested = [];
	private readonly VariableTree _tree;
	private DebugState _state = DebugState.Inactive;
	private bool _adding;

	public WatchView() : base("DebugWatch") => _tree = new VariableTree(this, () => Debugger);

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

		if (state == DebugState.Stopped && _state != DebugState.Stopped) Reevaluate();
		else if (state == DebugState.Inactive) {
			_tree.Clear();
			_results.Clear();
			_requested.Clear();
		}

		_state = state;
		Refresh();
	}

	private void Reevaluate() {
		_tree.NewStop();
		_results.Clear();
		_requested.Clear();
	}

	public void Add() {
		_adding = true;
		BeginEdit(NewKey, string.Empty, text => {
			_adding = false;

			if (text.Trim() is { Length: > 0 } expression) Debugger?.AddWatchExpression(expression);
		}, () => _adding = false);
	}

	public void CollapseAll() => _tree.CollapseAll();

	protected override void Build(List<DebugRow> rows) {
		if (Debugger is not { } debugger) return;

		IReadOnlyList<string> watches = debugger.GetWatchExpressions();
		bool stopped = debugger.GetState() == DebugState.Stopped;

		for (int i = 0; i < watches.Count; i++) {
			string expression = watches[i];
			int index = i;
			string path = $"watch:{i}:{expression}";
			DebugVariable? result = stopped ? Result(debugger, expression) : null;
			bool expandable = result is { VariablesReference: > 0 };
			bool expanded = expandable && _tree.IsExpanded(path);
			string value = !stopped ? string.Empty : result is null ? "..." : result.Value;

			rows.Add(new DebugRow {
				Depth = 0,
				Label = expression,
				LabelColor = Theme.DebugTokenName,
				Value = stopped ? value : null,
				ValueColor = result is null ? Theme.DescriptionForeground : DebugValueColors.For(result.Value, result.Type),
				Highlighted = result is not null && _tree.Changed(path, result.Value),
				Expandable = expandable,
				Expanded = expanded,
				Toggle = expandable ? () => _tree.Toggle(path) : null,
				Activate = expandable ? () => _tree.Toggle(path) : () => { },
				DoubleActivate = () => Rename(index, expression),
				EditKey = $"watch:{i}",
				Actions = [new DebugRowAction(Codicon.Edit, "Edit Expression", () => Rename(index, expression)), new DebugRowAction(Codicon.Close, "Remove Expression", () => debugger.RemoveWatchExpression(index))],
				Context = menu => {
					menu.Add(new MenuItem("Edit Expression", null, () => Rename(index, expression)));
					menu.Add(new MenuItem("Copy Expression", null, () => Forge.Core.Native.Clipboard.SetText(expression)));

					if (result is not null) menu.Add(new MenuItem("Copy Value", null, () => Forge.Core.Native.Clipboard.SetText(result.Value)));

					menu.Add(new MenuItem("Remove Expression", null, () => debugger.RemoveWatchExpression(index)));
				},
			});

			if (expanded && result is not null) _tree.AddChildren(rows, result.VariablesReference, path, 1, Context, (_, _, _) => { });
		}

		if (_adding) rows.Add(new DebugRow { Depth = 0, Label = string.Empty, Separator = null, EditKey = NewKey });
		else if (watches.Count == 0) Message(rows, "Add an expression with + or right-click a variable");
	}

	private DebugVariable? Result(IDebug debugger, string expression) {
		if (_results.TryGetValue(expression, out DebugVariable? result)) return result;

		if (_requested.Add(expression)) _ = debugger.EvaluateAsync(expression, "watch").ContinueWith(task => {
			_results[expression] = task.IsCompletedSuccessfully && task.Result is { } value ? value : new DebugVariable(expression, "<error: not available>", null, 0, expression, true, 0, 0);
			Refresh();
		}, TaskScheduler.Default);

		return null;
	}

	private void Rename(int index, string expression) => BeginEdit($"watch:{index}", expression, text => {
		if (text.Trim() is { Length: > 0 } renamed) Debugger?.RenameWatchExpression(index, renamed);
		else Debugger?.RemoveWatchExpression(index);

		_results.TryRemove(expression, out _);
		_requested.Remove(expression);
	});

	private void Context(Menu menu, DebugVariable variable, int container, string path) {
		if (Debugger is { } debugger) VariableTree.AddValueItems(menu, variable, debugger, null);
	}

	protected override void AddGeneralContext(Menu menu) {
		if (Debugger is not { } debugger) return;

		if (menu.Children.Count > 0) menu.AddSeparator();

		menu.Add(new MenuItem("Add Expression", null, Add));
		menu.Add(new MenuItem("Hex Display", null, () => {
			debugger.HexDisplay = !debugger.HexDisplay;
			Reevaluate();
			Refresh();
		}) { Checked = debugger.HexDisplay });
		menu.Add(new MenuItem("Collapse All", null, CollapseAll));
		menu.Add(new MenuItem("Remove All Expressions", null, debugger.RemoveAllWatchExpressions));
	}
}
