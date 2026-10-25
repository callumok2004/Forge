namespace Forge.Shell;

public sealed class NavigationHistory(Workbench workbench)
{
	private const int MaxStackSize = 50;
	private const int SelectionThreshold = 10;

	private readonly record struct Entry(string Path, int Anchor, int Caret, int Line);

	private readonly List<Entry> _stack = [];
	private int _index = -1;
	private Entry? _current;
	private bool _navigating;
	private bool _jumping;

	public bool CanGoBack => _index > 0;

	public bool CanGoForward => _index < _stack.Count - 1;

	public void MarkJump() => _jumping = true;

	public void Observe() {
		if (workbench.ActiveDocument?.Path is not { } path || workbench.ActiveView is not { } view) return;

		int line = Math.Min(view.Snapshot.GetLineAt(view.SelectionAnchor), view.Snapshot.GetLineAt(view.CaretOffset));
		Entry candidate = new(path, view.SelectionAnchor, view.CaretOffset, line);

		if (_current == candidate) return;

		bool jump = _jumping;
		_jumping = false;

		if (!_navigating) {
			if (_current is not { } previous || Justifies(previous, candidate, jump)) Add(candidate);
			else if (_index >= 0) _stack[_index] = candidate;
		}

		_current = candidate;
	}

	private static bool Justifies(Entry previous, Entry candidate, bool jump) {
		if (!string.Equals(previous.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)) return true;

		int distance = Math.Abs(previous.Line - candidate.Line);

		if (distance == 0) return false;

		return distance >= SelectionThreshold || jump;
	}

	private void Add(Entry entry) {
		if (_stack.Count > _index + 1) _stack.RemoveRange(_index + 1, _stack.Count - _index - 1);

		_stack.Add(entry);

		if (_stack.Count > MaxStackSize) _stack.RemoveAt(0);

		_index = _stack.Count - 1;
	}

	public void GoBack() {
		if (CanGoBack) Navigate(_index - 1);
	}

	public void GoForward() {
		if (CanGoForward) Navigate(_index + 1);
	}

	private void Navigate(int index) {
		Entry entry = _stack[index];

		_index = index;
		_navigating = true;

		try {
			workbench.OpenFile(entry.Path);

			if (workbench.ActiveView is not { } view || !string.Equals(workbench.ActiveDocument?.Path, entry.Path, StringComparison.OrdinalIgnoreCase)) {
				_stack.RemoveAt(index);
				_index = Math.Min(_index, _stack.Count - 1);
				return;
			}

			int length = view.Snapshot.Length;

			view.PushUndoStop();
			view.MoveTo(Math.Min(entry.Anchor, length), extend: false);
			view.MoveTo(Math.Min(entry.Caret, length), extend: true);
			workbench.RevealCaret?.Invoke();

			_current = new Entry(entry.Path, view.SelectionAnchor, view.CaretOffset, Math.Min(view.Snapshot.GetLineAt(view.SelectionAnchor), view.Snapshot.GetLineAt(view.CaretOffset)));
		}
		finally {
			_navigating = false;
		}
	}
}
