using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.Language;

public sealed class CodeActionWidget : Panel
{
	private const float RowHeight = 24f;
	private const float Padding = 4f;
	private const float IconColumn = 26f;
	private const float RightPadding = 12f;
	private const float MinWidth = 100f;

	private readonly record struct Group(string Kind, string Title, Codicon? Icon);

	private readonly record struct Row(bool IsHeader, string Text, Codicon? Icon, int Action, bool Disabled);

	private static readonly Group[] Groups = [
		new("quickfix", "Quick Fix", null),
		new("refactor.extract", "Extract", Codicon.Wrench),
		new("refactor.inline", "Inline", Codicon.Wrench),
		new("refactor.rewrite", "Rewrite", Codicon.Wrench),
		new("refactor.move", "Move", Codicon.Wrench),
		new("surround", "Surround With", Codicon.SurroundWith),
		new("source", "Source Action", Codicon.SymbolFile),
		new(string.Empty, "More Actions...", null),
	];

	private readonly List<Row> _rows = [];
	private int _selected = -1;

	public CodeActionWidget() {
		Name = "CodeActions";
		KeyboardInputEnabled = true;
	}

	public Action<int>? Accepted { get; set; }

	public Action? Closed { get; set; }

	public bool IsOpen => IsPopup;

	private static bool Contains(string group, string kind) => group.Length == 0 || kind == group || kind.StartsWith(group + ".", StringComparison.Ordinal);

	public void Show(IReadOnlyList<CodeActionEntry> actions, float x, float y) {
		_rows.Clear();

		List<CodeActionEntry>[] buckets = [.. Groups.Select(static _ => new List<CodeActionEntry>())];

		foreach (CodeActionEntry action in actions) {
			for (int i = 0; i < Groups.Length; i++) {
				if (!Contains(Groups[i].Kind, action.Kind)) continue;

				buckets[i].Add(action);
				break;
			}
		}

		for (int i = 0; i < Groups.Length; i++) {
			if (buckets[i].Count == 0) continue;

			_rows.Add(new Row(true, Groups[i].Title, null, -1, false));

			foreach (CodeActionEntry action in buckets[i]) _rows.Add(new Row(false, action.DisabledReason ?? action.Title, Groups[i].Icon, action.Index, action.DisabledReason is not null));
		}

		_selected = Next(-1, 1);

		if (Ui is null || _rows.Count == 0) return;

		float scale = Scale;
		ITextFont font = Ui.GetFonts().GetUi();
		float widest = MinWidth * scale;

		foreach (Row row in _rows) widest = Math.Max(widest, font.Measure(row.Text) + ((IconColumn + RightPadding + Padding * 2f) * scale));

		float height = (_rows.Count * RowHeight * scale) + (Padding * 2f * scale);
		float left = Math.Max(0f, Math.Min(x, Ui.GetViewportWidth() - widest));
		float top = y + height > Ui.GetViewportHeight() ? Math.Max(0f, y - height) : y;

		SetBounds(left, top, widest, height);
		MakePopup();
		RequestFocus();
		Repaint();
	}

	public void Hide() {
		if (!IsPopup) return;

		ClosePopup();
		Closed?.Invoke();
	}

	public override void OnPopupDismissed() => Hide();

	private int Next(int from, int direction) {
		for (int step = 1; step <= _rows.Count; step++) {
			int index = (((from + (direction * step)) % _rows.Count) + _rows.Count) % _rows.Count;

			if (!_rows[index].IsHeader && !_rows[index].Disabled) return index;
		}

		return -1;
	}

	private int RowAt(float y) {
		int index = (int)((y - (Padding * Scale)) / (RowHeight * Scale));
		return index >= 0 && index < _rows.Count ? index : -1;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		switch (key) {
			case VirtualKey.Escape:
				Hide();
				return true;

			case VirtualKey.Up:
				_selected = Next(_selected, -1);
				Repaint();
				return true;

			case VirtualKey.Down:
				_selected = Next(_selected, 1);
				Repaint();
				return true;

			case VirtualKey.Return:
				Accept(_selected);
				return true;
		}

		return true;
	}

	public override void OnCursorMoved(float x, float y) {
		int row = RowAt(y);

		if (row < 0 || row == _selected || _rows[row].IsHeader || _rows[row].Disabled) return;

		_selected = row;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) => true;

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (button == MouseButton.Left) Accept(RowAt(y));

		return true;
	}

	private void Accept(int row) {
		if (row < 0 || row >= _rows.Count || _rows[row].IsHeader || _rows[row].Disabled) return;

		int action = _rows[row].Action;
		Hide();
		Accepted?.Invoke(action);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusMedium * scale;
		ITextFont font = painter.GetUi();
		float rowHeight = RowHeight * scale;
		float padding = Padding * scale;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);

		for (int i = 0; i < _rows.Count; i++) {
			Row row = _rows[i];
			float top = ScreenY + padding + (i * rowHeight);
			float baseline = top + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();

			if (row.IsHeader) {
				painter.DrawText(font, row.Text, ScreenX + padding + (Metrics.Space8 * scale), baseline, Theme.DescriptionForeground);
				continue;
			}

			if (i == _selected) painter.FillRoundedRectangle(ScreenX + padding, top, Wide - (padding * 2f), rowHeight, Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);

			Color foreground = row.Disabled ? Theme.DisabledForeground : Theme.Foreground;
			Codicon icon = row.Icon ?? Codicon.LightBulb;
			Color iconColor = row.Disabled ? Theme.DisabledForeground : row.Icon is null ? Theme.EditorLightBulbForeground : Theme.Foreground;

			painter.DrawIcon(icon, ScreenX + padding + (IconColumn * scale / 2f), top + (rowHeight / 2f), Metrics.IconSize * scale, iconColor);
			painter.DrawText(font, row.Text, ScreenX + padding + (IconColumn * scale), baseline, foreground);
		}
	}
}
