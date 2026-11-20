using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed record DebugRowAction(Codicon Icon, string Title, Action Run);

public sealed class DebugRow
{
	public int Depth { get; init; }

	public string Label { get; init; } = string.Empty;

	public Color LabelColor { get; init; } = Theme.SidebarFileForeground;

	public bool Bold { get; init; }

	public string? Separator { get; init; } = ": ";

	public string? Value { get; init; }

	public Color ValueColor { get; init; } = Theme.DebugTokenValue;

	public string? Detail { get; init; }

	public string? Badge { get; init; }

	public Codicon Icon { get; init; }

	public Color IconColor { get; init; } = Theme.SidebarFileForeground;

	public bool Expandable { get; init; }

	public bool Expanded { get; init; }

	public bool Dimmed { get; init; }

	public bool Highlighted { get; init; }

	public bool Focused { get; init; }

	public bool? Checked { get; init; }

	public IReadOnlyList<DebugRowAction> Actions { get; init; } = [];

	public Action? Toggle { get; init; }

	public Action? Activate { get; init; }

	public Action? DoubleActivate { get; init; }

	public Action? CheckToggled { get; init; }

	public Action<Menu>? Context { get; init; }

	public string? EditKey { get; init; }

	public string CopyText => string.Concat(Label, Value is null ? string.Empty : string.Concat(Separator ?? string.Empty, Value), Detail is { Length: > 0 } detail ? string.Concat(" (", detail, ")") : string.Empty);
}

public abstract class DebugTreeView : Panel
{
	private const float RowHeight = 22f;
	private const float Indent = 12f;
	private const float Twistie = 16f;
	private const float CheckSize = 14f;
	private const float IconWidth = 18f;
	private const float ActionSize = 20f;
	private const float PaddingX = 8f;

	private readonly List<DebugRow> _rows = [];
	private readonly TextEntry _editor = new() { FontSize = 13f, Visible = false };
	private Action<string>? _commitEdit;
	private Action? _cancelEdit;
	private string? _editKey;
	private int _editRow = -1;
	private volatile bool _stale = true;
	private float _scrollY;
	private int _hover = -1;
	private int _hoverAction = -1;
	private int _pressed = -1;
	private readonly SortedSet<int> _selection = [];
	private int _anchor = -1;

	protected DebugTreeView(string name) {
		Name = name;
		Dock = DockType.Fill;
		KeyboardInputEnabled = true;
		_editor.Submitted = () => FinishEdit(commit: true);
		_editor.FocusLost = () => FinishEdit(commit: true);
		AddChild(_editor);
	}

	protected abstract void Build(List<DebugRow> rows);

	public void Refresh() {
		_stale = true;
		Repaint();
	}

	protected static void Message(List<DebugRow> rows, string text) => rows.Add(new DebugRow { Depth = 0, Label = text, LabelColor = Theme.DescriptionForeground, Separator = null });

	protected void BeginEdit(string key, string text, Action<string> commit, Action? cancel = null) {
		_editKey = key;
		_commitEdit = commit;
		_cancelEdit = cancel;
		_editor.SetText(text);
		Refresh();
	}

	private void FinishEdit(bool commit) {
		if (_editKey is null) return;

		Action<string>? apply = _commitEdit;
		Action? cancel = _cancelEdit;
		string text = _editor.GetString();
		_editKey = null;
		_commitEdit = null;
		_cancelEdit = null;
		_editRow = -1;
		_editor.Visible = false;

		if (commit) apply?.Invoke(text);
		else cancel?.Invoke();

		Refresh();
	}

	private void EnsureRows() {
		if (!_stale) return;

		_stale = false;
		int previous = _rows.Count;
		_rows.Clear();
		Build(_rows);

		if (_rows.Count != previous) _selection.Clear();
		_editRow = _editKey is null ? -1 : _rows.FindIndex(row => row.EditKey == _editKey);
		_hover = Math.Min(_hover, _rows.Count - 1);
		_selection.RemoveWhere(index => index >= _rows.Count);
		ClampScroll();
		PlaceEditor();
	}

	private void PlaceEditor() {
		if (_editRow < 0) {
			_editor.Visible = false;
			return;
		}

		float scale = Scale;
		float height = RowHeight * scale;
		float left = ContentLeft(_rows[_editRow], scale);
		_editor.Visible = true;
		_editor.SetBounds(left, (_editRow * height) - _scrollY + (1f * scale), Math.Max(40f * scale, Wide - left - (PaddingX * scale)), height - (2f * scale));

		if (!_editor.HasFocus) _editor.RequestFocus();
	}

	public override void PerformLayout() {
		EnsureRows();
		PlaceEditor();
		base.PerformLayout();
	}

	private float ContentLeft(DebugRow row, float scale) {
		float x = (PaddingX + (row.Depth * Indent) + Twistie) * scale;

		if (row.Checked is not null) x += (CheckSize + 6f) * scale;

		if (row.Icon != default) x += IconWidth * scale;

		return x;
	}

	private void ClampScroll() => _scrollY = Math.Clamp(_scrollY, 0f, Math.Max(0f, (_rows.Count * RowHeight * Scale) - Tall));

	private int RowAt(float y) {
		int index = (int)MathF.Floor((y + _scrollY) / (RowHeight * Scale));
		return index >= 0 && index < _rows.Count ? index : -1;
	}

	private float ActionLeft(int index, int count, float scale) => Wide - ((PaddingX + ((count - index) * ActionSize)) * scale);

	private int ActionAt(int row, float x) {
		if (row < 0 || row != _hover) return -1;

		IReadOnlyList<DebugRowAction> actions = _rows[row].Actions;
		float scale = Scale;

		for (int i = 0; i < actions.Count; i++) {
			float left = ActionLeft(i, actions.Count, scale);

			if (x >= left && x < left + (ActionSize * scale)) return i;
		}

		return -1;
	}

	private bool InTwistie(DebugRow row, float x) {
		float scale = Scale;
		float left = (PaddingX + (row.Depth * Indent)) * scale;
		return row.Expandable && x >= left && x < left + (Twistie * scale);
	}

	private bool InCheck(DebugRow row, float x) {
		if (row.Checked is null) return false;

		float scale = Scale;
		float left = (PaddingX + (row.Depth * Indent) + Twistie) * scale;
		return x >= left - (2f * scale) && x < left + ((CheckSize + 4f) * scale);
	}

	public override void OnCursorMoved(float x, float y) {
		EnsureRows();

		int row = RowAt(y);
		int action = ActionAt(row, x);

		if (row == _hover && action == _hoverAction) return;

		RepaintRow(_hover);
		_hover = row;
		_hoverAction = ActionAt(row, x);
		RepaintRow(_hover);
	}

	public override void OnCursorExited() {
		RepaintRow(_hover);
		_hover = -1;
		_hoverAction = -1;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		EnsureRows();

		int index = RowAt(y);

		if (button == MouseButton.Right) {
			if (index >= 0 && !_selection.Contains(index)) Select(index, KeyModifiers.None);

			OpenContext(index, x, y);
			return true;
		}

		if (button != MouseButton.Left) return true;

		KeyModifiers modifiers = Ui?.GetModifiers() ?? KeyModifiers.None;
		RequestFocus();
		_pressed = (modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0 ? -1 : index;
		Select(index, modifiers);
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return true;

		int index = RowAt(y);

		if (index < 0) return true;

		DebugRow row = _rows[index];

		if (row.DoubleActivate is { } edit && !InTwistie(row, x) && !InCheck(row, x) && ActionAt(index, x) < 0) {
			_pressed = -1;
			edit();
			return true;
		}

		return OnMousePressed(button, x, y);
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return true;

		int index = RowAt(y);
		int pressed = _pressed;
		_pressed = -1;

		if (index < 0 || index != pressed) return true;

		DebugRow row = _rows[index];
		int action = ActionAt(index, x);

		if (action >= 0) row.Actions[action].Run();
		else if (InCheck(row, x)) row.CheckToggled?.Invoke();
		else if (InTwistie(row, x)) row.Toggle?.Invoke();
		else if (row.Activate is { } activate) activate();
		else row.Toggle?.Invoke();

		Repaint();
		return true;
	}

	private void Select(int index, KeyModifiers modifiers) {
		if (index < 0) {
			if ((modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0) _selection.Clear();

			Repaint();
			return;
		}

		if ((modifiers & KeyModifiers.Shift) != 0 && _anchor >= 0) {
			if ((modifiers & KeyModifiers.Control) == 0) _selection.Clear();

			for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selection.Add(i);
		}
		else if ((modifiers & KeyModifiers.Control) != 0) {
			if (!_selection.Remove(index)) _selection.Add(index);

			_anchor = index;
		}
		else {
			_selection.Clear();
			_selection.Add(index);
			_anchor = index;
		}

		Repaint();
	}

	private string SelectedText() => string.Join(Environment.NewLine, _selection.Where(index => index < _rows.Count).Select(index => _rows[index].CopyText));

	private void OpenContext(int index, float x, float y) {
		Menu menu = new();

		if (_selection.Count > 1) menu.Add(new MenuItem($"Copy {_selection.Count} Rows", null, () => Forge.Core.Native.Clipboard.SetText(SelectedText())));
		else if (index >= 0) _rows[index].Context?.Invoke(menu);

		if (index >= 0 && _selection.Count <= 1) menu.Add(new MenuItem("Copy", null, () => Forge.Core.Native.Clipboard.SetText(_rows[index].CopyText)));

		AddGeneralContext(menu);

		if (menu.Children.Count == 0) return;

		(float absX, float absY) = GetAbsPos();
		menu.Open(absX + x, absY + y);
	}

	protected virtual void AddGeneralContext(Menu menu) { }

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float previous = _scrollY;
		_scrollY -= delta / 120f * 3f * RowHeight * Scale;
		ClampScroll();

		if (_scrollY != previous) {
			PlaceEditor();
			Repaint();
		}

		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if ((modifiers & KeyModifiers.Control) != 0 && key == Forge.Core.Native.VirtualKey.C && _selection.Count > 0 && _editKey is null) {
			Forge.Core.Native.Clipboard.SetText(SelectedText());
			return true;
		}

		if ((modifiers & KeyModifiers.Control) != 0 && key == Forge.Core.Native.VirtualKey.A && _editKey is null) {
			for (int i = 0; i < _rows.Count; i++) _selection.Add(i);

			Repaint();
			return true;
		}

		if (key != Forge.Core.Native.VirtualKey.Escape || _editKey is null) return false;

		FinishEdit(commit: false);
		return true;
	}

	private void RepaintRow(int row) {
		if (row < 0) return;

		float height = RowHeight * Scale;
		Repaint(0f, (row * height) - _scrollY, Wide, height);
	}

	public override void Paint(IPainter painter) {
		EnsureRows();

		float scale = Scale;
		float height = RowHeight * scale;
		int first = Math.Max(0, (int)(_scrollY / height));

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		for (int i = first; i < _rows.Count; i++) {
			float top = ScreenY + (i * height) - _scrollY;

			if (top > ScreenY + Tall) break;

			if (!painter.NeedsPaint(ScreenX, top, Wide, height)) continue;

			PaintRow(painter, i, top, height, scale);
		}

		painter.PopClip();
	}

	private void PaintRow(IPainter painter, int index, float top, float height, float scale) {
		DebugRow row = _rows[index];
		bool hovered = index == _hover;
		ITextFont font = painter.GetUi();
		ITextFont labelFont = row.Bold ? painter.GetUiStrong() : font;
		float middle = top + (height / 2f);
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float inset = 4f * scale;

		if (_selection.Contains(index)) painter.FillRoundedRectangle(ScreenX + inset, top, Wide - (inset * 2f), height, Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);
		else if (row.Focused) painter.FillRoundedRectangle(ScreenX + inset, top, Wide - (inset * 2f), height, Metrics.RadiusSmall * scale, Theme.DebugFocusedFrameBackground);
		else if (row.Highlighted) painter.FillRoundedRectangle(ScreenX + inset, top, Wide - (inset * 2f), height, Metrics.RadiusSmall * scale, Theme.DebugValueChanged);
		else if (hovered) painter.FillRoundedRectangle(ScreenX + inset, top, Wide - (inset * 2f), height, Metrics.RadiusSmall * scale, Theme.ListHoverBackground);

		float x = ScreenX + ((PaddingX + (row.Depth * Indent)) * scale);

		if (row.Expandable) painter.DrawChevron(x + (Twistie * scale / 2f), middle, 8f * scale, row.Expanded, Theme.SidebarFileForeground);

		x += Twistie * scale;

		if (row.Checked is { } on) {
			float box = CheckSize * scale;
			float boxTop = middle - (box / 2f);
			painter.FillRoundedRectangle(x, boxTop, box, box, 3f * scale, on ? Theme.FocusBorder : Theme.CheckboxBackground);
			painter.StrokeRoundedRectangle(x, boxTop, box, box, 3f * scale, scale, Theme.CheckboxBorder);

			if (on) painter.DrawIcon(Codicon.Check, x + (box / 2f), middle, box * 0.85f, Theme.CheckboxForeground);

			x += (CheckSize + 6f) * scale;
		}

		if (row.Icon != default) {
			painter.DrawIcon(row.Icon, x + (7f * scale), middle, 14f * scale, row.IconColor);
			x += IconWidth * scale;
		}

		if (index == _editRow) return;

		float right = ScreenX + Wide - (PaddingX * scale);

		if (hovered && row.Actions.Count > 0) {
			for (int i = 0; i < row.Actions.Count; i++) {
				float left = ScreenX + ActionLeft(i, row.Actions.Count, scale);

				if (i == _hoverAction) painter.FillRoundedRectangle(left, middle - (ActionSize * scale / 2f), ActionSize * scale, ActionSize * scale, Metrics.RadiusSmall * scale, Theme.ToolbarHoverBackground);

				painter.DrawIcon(row.Actions[i].Icon, left + (ActionSize * scale / 2f), middle, 14f * scale, Theme.SidebarFileForeground);
			}

			right = ScreenX + ActionLeft(0, row.Actions.Count, scale) - (4f * scale);
		}
		else {
			if (row.Detail is { Length: > 0 } detail) {
				float width = font.Measure(detail);
				painter.DrawText(font, detail, right - width, baseline, Theme.DescriptionForeground);
				right -= width + (8f * scale);
			}

			if (row.Badge is { Length: > 0 } badge) {
				ITextFont small = painter.GetUiSmall();
				float width = small.Measure(badge) + (8f * scale);
				float badgeHeight = small.GetLineHeight() + (2f * scale);
				painter.FillRoundedRectangle(right - width, middle - (badgeHeight / 2f), width, badgeHeight, 3f * scale, Theme.DebugStateLabelBackground);
				painter.DrawText(small, badge, right - width + (4f * scale), middle - (small.GetLineHeight() / 2f) + small.GetBaseline(), Theme.SidebarFileForeground);
				right -= width + (6f * scale);
			}
		}

		float alpha = row.Dimmed ? 0.6f : 1f;
		painter.PushClip(x, top, Math.Max(0f, right - x), height);
		painter.DrawText(labelFont, row.Label, x, baseline, row.LabelColor.WithAlpha(alpha));

		if (row.Value is { } value) {
			float valueX = x + labelFont.Measure(row.Label);

			if (row.Separator is { Length: > 0 } separator) {
				painter.DrawText(font, separator, valueX, baseline, Theme.DescriptionForeground.WithAlpha(alpha));
				valueX += font.Measure(separator);
			}

			painter.DrawText(font, value, valueX, baseline, row.ValueColor.WithAlpha(alpha));
		}

		painter.PopClip();
	}
}
