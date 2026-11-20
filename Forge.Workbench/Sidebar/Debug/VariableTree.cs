using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class VariableTree(DebugTreeView owner, Func<IDebug?> debugger)
{
	private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IReadOnlyList<DebugVariable>> _children = [];
	private readonly HashSet<int> _loading = [];
	private readonly HashSet<string> _expanded = [];
	private readonly Dictionary<string, string> _previous = [];
	private readonly Dictionary<string, string> _current = [];

	public void Expand(string path) => _expanded.Add(path);

	public bool IsExpanded(string path) => _expanded.Contains(path);

	public void Toggle(string path) {
		if (!_expanded.Remove(path)) _expanded.Add(path);

		owner.Refresh();
	}

	public void CollapseAll() {
		_expanded.Clear();
		owner.Refresh();
	}

	public void NewStop() {
		_previous.Clear();

		foreach ((string path, string value) in _current) _previous[path] = value;

		_current.Clear();
		_children.Clear();
		_loading.Clear();
	}

	public void Clear() {
		_previous.Clear();
		_current.Clear();
		_children.Clear();
		_loading.Clear();
	}

	public IReadOnlyList<DebugVariable>? Children(int reference) {
		if (_children.TryGetValue(reference, out IReadOnlyList<DebugVariable>? children)) return children;

		if (debugger() is not { } debug || !_loading.Add(reference)) return null;

		_ = debug.VariablesAsync(reference).ContinueWith(task => {
			_children[reference] = task.IsCompletedSuccessfully ? task.Result : [];
			owner.Refresh();
		}, TaskScheduler.Default);

		return null;
	}

	public void Invalidate(int reference) {
		_children.TryRemove(reference, out _);
		_loading.Remove(reference);
	}

	public bool Changed(string path, string value) {
		_current[path] = value;
		return _previous.TryGetValue(path, out string? before) && before != value;
	}

	public void AddChildren(List<DebugRow> rows, int reference, string path, int depth, Action<Menu, DebugVariable, int, string> context, Action<DebugVariable, int, string> edit) {
		if (Children(reference) is not { } children) {
			rows.Add(new DebugRow { Depth = depth, Label = "Loading...", LabelColor = Theme.DescriptionForeground, Separator = null });
			return;
		}

		foreach (DebugVariable child in children) AddVariable(rows, child, reference, $"{path}/{child.Name}", depth, context, edit);
	}

	public void AddVariable(List<DebugRow> rows, DebugVariable variable, int container, string path, int depth, Action<Menu, DebugVariable, int, string> context, Action<DebugVariable, int, string> edit) {
		bool expandable = variable.VariablesReference > 0;
		bool expanded = expandable && _expanded.Contains(path);
		bool editable = !variable.ReadOnly && container > 0;

		rows.Add(new DebugRow {
			Depth = depth,
			Label = variable.Name,
			LabelColor = Theme.DebugTokenName,
			Value = variable.Value,
			ValueColor = DebugValueColors.For(variable.Value, variable.Type),
			Highlighted = Changed(path, variable.Value),
			Expandable = expandable,
			Expanded = expanded,
			Toggle = expandable ? () => Toggle(path) : null,
			Activate = expandable ? () => Toggle(path) : () => { },
			DoubleActivate = editable ? () => edit(variable, container, path) : null,
			EditKey = path,
			Context = menu => context(menu, variable, container, path),
		});

		if (expanded) AddChildren(rows, variable.VariablesReference, path, depth + 1, context, edit);
	}

	public static void AddValueItems(Menu menu, DebugVariable variable, IDebug debug, Action? setValue) {
		menu.Add(new MenuItem("Copy Value", null, () => Clipboard.SetText(variable.Value)));

		if (variable.EvaluateName is { Length: > 0 } expression) {
			menu.Add(new MenuItem("Copy as Expression", null, () => Clipboard.SetText(expression)));
			menu.Add(new MenuItem("Add to Watch", null, () => debug.AddWatchExpression(expression)));
		}

		if (setValue is not null) menu.Add(new MenuItem("Set Value", null, setValue));
	}
}

public static class DebugValueColors
{
	public static Color For(string value, string? type) {
		if (value.Length == 0) return Theme.DebugTokenValue;

		if (value.StartsWith('<') && value.Contains("error", StringComparison.OrdinalIgnoreCase)) return Theme.DebugTokenError;

		if (value[0] is '"' or '\'' || type is "string" or "char") return Theme.DebugTokenString;

		if (value is "true" or "false" or "null" || type == "bool") return Theme.DebugTokenBoolean;

		if (char.IsDigit(value[0]) || (value[0] == '-' && value.Length > 1 && char.IsDigit(value[1]))) return Theme.DebugTokenNumber;

		return Theme.DebugTokenValue;
	}
}
