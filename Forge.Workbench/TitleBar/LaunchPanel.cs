using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class LaunchPanel : Panel
{
	private const float Width = 340f;
	private const float RowHeight = 24f;
	private const float HeaderHeight = 26f;
	private const float SeparatorHeight = 9f;
	private const float PaddingY = 4f;
	private const float PaddingX = 10f;
	private const float ValueWidth = 96f;
	private const float CheckSize = 14f;

	private enum RowKind { Header, Configuration, Option, Empty, Separator, Edit, Add, Adding }

	private readonly record struct Row(RowKind Kind, string Text, string Detail, int Index);

	private readonly IDebug _debugger;
	private readonly Action _edit;
	private readonly List<Row> _rows = [];
	private readonly Dictionary<int, TextEntry> _values = [];
	private float _anchorX;
	private float _anchorY;
	private int _hover = -1;
	private bool _adding;
	private TextEntry? _addEntry;
	private const float RemoveSize = 22f;

	public LaunchPanel(IDebug debugger, Action edit) {
		_debugger = debugger;
		_edit = edit;
		Name = "LaunchPanel";
		KeyboardInputEnabled = true;
	}

	public override bool DismissOnFocusLost => false;

	public void Open(float x, float y) {
		_anchorX = x;
		_anchorY = y;
		Rebuild();
		MakePopup();
		RequestFocus();
	}

	public void Close() {
		Commit();
		ClosePopup();
	}

	public override void OnPopupDismissed() => Close();

	private DebugConfiguration? Selected {
		get {
			string? name = _debugger.GetSelectedConfiguration();

			foreach (DebugConfiguration configuration in _debugger.GetConfigurations()) {
				if (configuration.Name == name) return configuration;
			}

			return null;
		}
	}

	private void Rebuild() {
		_rows.Clear();

		foreach (TextEntry entry in _values.Values) RemoveChild(entry);

		_values.Clear();

		_rows.Add(new Row(RowKind.Header, "Launch Configuration", string.Empty, -1));

		IReadOnlyList<DebugConfiguration> configurations = _debugger.GetConfigurations();

		for (int i = 0; i < configurations.Count; i++) _rows.Add(new Row(RowKind.Configuration, configurations[i].Name, string.Empty, i));

		if (Selected is { } selected) {
			_rows.Add(new Row(RowKind.Separator, string.Empty, string.Empty, -1));
			_rows.Add(new Row(RowKind.Header, "Options", string.Empty, -1));

			if (selected.Options.Count == 0 && !_adding) _rows.Add(new Row(RowKind.Empty, "No options yet", string.Empty, -1));

			for (int i = 0; i < selected.Options.Count; i++) {
				LaunchOption option = selected.Options[i];
				string detail = string.Join(' ', option.Args.Select(static arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));
				_rows.Add(new Row(RowKind.Option, option.Label, detail == option.Label ? string.Empty : detail, i));

				if (option.Value is null) continue;

				int index = i;
				TextEntry entry = new() { FontSize = 12f };
				entry.SetText(option.Value);
				entry.Submitted = () => CommitValue(index, entry);
				entry.FocusLost = () => CommitValue(index, entry);
				_values[i] = entry;
				AddChild(entry);
			}
		}

		if (_addEntry is not null) {
			RemoveChild(_addEntry);
			_addEntry = null;
		}

		if (Selected is not null) {
			if (_adding) {
				TextEntry entry = new() { FontSize = 12f, Placeholder = "-myparam 123 or \"+myparam wow\"" };
				entry.Submitted = SubmitAdd;
				_addEntry = entry;
				AddChild(entry);
				_rows.Add(new Row(RowKind.Adding, string.Empty, string.Empty, -1));
			}
			else _rows.Add(new Row(RowKind.Add, "Add Option...", string.Empty, -1));
		}

		_rows.Add(new Row(RowKind.Separator, string.Empty, string.Empty, -1));
		_rows.Add(new Row(RowKind.Edit, "Edit Configurations...", string.Empty, -1));
		InvalidateLayout();
		Repaint();
	}

	private void SubmitAdd() {
		if (_addEntry is not { } entry || Selected is not { } selected) return;

		string text = entry.GetString();
		_adding = false;

		if (text.Trim().Length > 0) _debugger.AddLaunchOption(selected.Name, text);

		Rebuild();
		RequestFocus();
	}

	private void Commit() {
		foreach ((int index, TextEntry entry) in _values) CommitValue(index, entry);
	}

	private void CommitValue(int index, TextEntry entry) {
		if (Selected is not { } selected || (uint)index >= (uint)selected.Options.Count) return;

		LaunchOption option = selected.Options[index];
		string value = entry.GetString();

		if (value == option.Value) return;

		_debugger.SetLaunchOption(selected.Name, index, option.Enabled, value);
	}

	private static float HeightOf(RowKind kind) => kind switch {
		RowKind.Header => HeaderHeight,
		RowKind.Separator => SeparatorHeight,
		_ => RowHeight,
	};

	public override void PerformLayout() {
		if (Ui is null) return;

		float scale = Scale;
		float height = PaddingY * 2f * scale;

		foreach (Row row in _rows) height += HeightOf(row.Kind) * scale;

		float width = Width * scale;
		float x = MathF.Max(0f, MathF.Min(_anchorX, Ui.GetViewportWidth() - width));
		float y = MathF.Min(_anchorY, MathF.Max(Ui.GetViewportHeight() - height, 0f));

		SetBounds(x, y, width, height);

		float top = PaddingY * scale;

		foreach (Row row in _rows) {
			float rowHeight = HeightOf(row.Kind) * scale;

			if (row.Kind == RowKind.Adding && _addEntry is { } adding) adding.SetBounds(PaddingX * scale, top + (2f * scale), width - (PaddingX * 2f * scale), rowHeight - (4f * scale));

			if (row.Kind == RowKind.Option && _values.TryGetValue(row.Index, out TextEntry? entry)) {
				float entryHeight = rowHeight - (4f * scale);
				entry.SetBounds(width - ((ValueWidth + PaddingX) * scale), top + (2f * scale), ValueWidth * scale, entryHeight);
			}

			top += rowHeight;
		}

		base.PerformLayout();
	}

	private int RowAt(float y) {
		float scale = Scale;
		float top = PaddingY * scale;

		for (int i = 0; i < _rows.Count; i++) {
			float height = HeightOf(_rows[i].Kind) * scale;

			if (y >= top && y < top + height) return i;

			top += height;
		}

		return -1;
	}

	private static bool Interactive(RowKind kind) => kind is RowKind.Configuration or RowKind.Option or RowKind.Edit or RowKind.Add;

	private float RemoveLeft(int index) => Wide - ((PaddingX + RemoveSize + (_values.ContainsKey(index) ? ValueWidth + 4f : 0f)) * Scale);

	public override void OnCursorMoved(float x, float y) {
		int row = RowAt(y);

		if (row >= 0 && !Interactive(_rows[row].Kind)) row = -1;

		if (row == _hover) return;

		_hover = row;
		Repaint();
	}

	public override void OnCursorExited() {
		if (_hover < 0) return;

		_hover = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return true;

		int index = RowAt(y);

		if (index < 0) return true;

		Row row = _rows[index];

		switch (row.Kind) {
			case RowKind.Configuration:
				Commit();
				_debugger.SelectConfiguration(row.Text);
				Rebuild();
				break;

			case RowKind.Add:
				_adding = true;
				Rebuild();
				_addEntry?.RequestFocus();
				break;

			case RowKind.Option when Selected is { } removing && (uint)row.Index < (uint)removing.Options.Count && x >= RemoveLeft(row.Index) && x < RemoveLeft(row.Index) + (RemoveSize * Scale):
				Commit();
				_debugger.RemoveLaunchOption(removing.Name, row.Index);
				Rebuild();
				break;

			case RowKind.Option when Selected is { } selected && (uint)row.Index < (uint)selected.Options.Count:
				LaunchOption option = selected.Options[row.Index];
				string? value = _values.TryGetValue(row.Index, out TextEntry? entry) ? entry.GetString() : option.Value;
				_debugger.SetLaunchOption(selected.Name, row.Index, !option.Enabled, value);
				Rebuild();
				break;

			case RowKind.Edit:
				Close();
				_edit();
				break;
		}

		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (key != VirtualKey.Escape) return false;

		if (_adding) {
			_adding = false;
			Rebuild();
			RequestFocus();
			return true;
		}

		Close();
		return true;
	}

	public override void PaintBackground(IPainter painter) => painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Theme.MenuBackground);

	public override void PaintBorder(IPainter painter) => painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Metrics.RadiusLarge * Scale, Metrics.StrokeThickness * Scale, Theme.MenuBorder);

	public override void Paint(IPainter painter) {
		if (Ui is null) return;

		float scale = Scale;
		ITextFont font = Ui.GetFonts().GetUi();
		ITextFont small = Ui.GetFonts().GetUi(11f, false);
		string? selectedName = _debugger.GetSelectedConfiguration();
		DebugConfiguration? selected = Selected;
		float left = ScreenX + (PaddingX * scale);
		float top = ScreenY + (PaddingY * scale);

		painter.PushClip(ScreenX, ScreenY, Wide, Tall);

		for (int i = 0; i < _rows.Count; i++) {
			Row row = _rows[i];
			float height = HeightOf(row.Kind) * scale;
			float middle = top + (height / 2f);
			float baseline = middle - (font.GetLineHeight() / 2f) + font.GetBaseline();

			if (i == _hover) painter.FillRoundedRectangle(ScreenX + (4f * scale), top, Wide - (8f * scale), height, Metrics.RadiusSmall * scale, Theme.MenuSelectionBackground);

			switch (row.Kind) {
				case RowKind.Header:
					painter.DrawText(small, row.Text.ToUpperInvariant(), left, middle - (small.GetLineHeight() / 2f) + small.GetBaseline(), Theme.DescriptionForeground);
					break;

				case RowKind.Separator:
					painter.FillRectangle(ScreenX, middle, Wide, scale, Theme.MenuBorder);
					break;

				case RowKind.Configuration:
					if (row.Text == selectedName) painter.DrawIcon(Codicon.Check, left + (CheckSize * scale / 2f), middle, CheckSize * scale, Theme.MenuForeground);

					painter.DrawText(font, row.Text, left + ((CheckSize + 8f) * scale), baseline, Theme.MenuForeground);
					break;

				case RowKind.Option when selected is not null && (uint)row.Index < (uint)selected.Options.Count:
					bool enabled = selected.Options[row.Index].Enabled;
					float box = CheckSize * scale;
					float boxTop = middle - (box / 2f);

					painter.FillRoundedRectangle(left, boxTop, box, box, 3f * scale, enabled ? Theme.FocusBorder : Theme.CheckboxBackground);
					painter.StrokeRoundedRectangle(left, boxTop, box, box, 3f * scale, scale, Theme.CheckboxBorder);

					if (enabled) painter.DrawIcon(Codicon.Check, left + (box / 2f), middle, box * 0.85f, Theme.MenuForeground);

					float textLeft = left + box + (8f * scale);
					float textRight = ScreenX + RemoveLeft(row.Index) - (4f * scale);

					if (i == _hover) painter.DrawIcon(Codicon.Close, ScreenX + RemoveLeft(row.Index) + (RemoveSize * scale / 2f), middle, CheckSize * scale, Theme.DescriptionForeground);

					painter.PushClip(textLeft, top, Math.Max(0f, textRight - textLeft), height);
					painter.DrawText(font, row.Text, textLeft, baseline, Theme.MenuForeground.WithAlpha(enabled ? 1f : 0.7f));
					painter.DrawText(small, row.Detail, textLeft + font.Measure(row.Text) + (8f * scale), middle - (small.GetLineHeight() / 2f) + small.GetBaseline(), Theme.DescriptionForeground);
					painter.PopClip();
					break;

				case RowKind.Empty:
					painter.DrawText(small, row.Text, left, middle - (small.GetLineHeight() / 2f) + small.GetBaseline(), Theme.DescriptionForeground);
					break;

				case RowKind.Add:
					painter.DrawIcon(Codicon.Add, left + (CheckSize * scale / 2f), middle, CheckSize * scale, Theme.DescriptionForeground);
					painter.DrawText(font, row.Text, left + ((CheckSize + 8f) * scale), baseline, Theme.DescriptionForeground);
					break;

				case RowKind.Edit:
					painter.DrawText(font, row.Text, left + ((CheckSize + 8f) * scale), baseline, Theme.MenuForeground);
					break;
			}

			top += height;
		}

		painter.PopClip();
	}
}
