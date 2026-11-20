using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public enum BreakpointEditMode
{
	Expression,
	HitCount,
	LogMessage,
}

public sealed class BreakpointEditor : Panel
{
	private const float Height = 30f;
	private const float ModeWidth = 104f;
	private const float PaddingX = 8f;

	private static readonly string[] ModeLabels = ["Expression", "Hit Count", "Log Message"];

	private static readonly string[] Placeholders = ["Break when expression evaluates to true", "Break when hit count condition is met, e.g. >= 5", "Message to log when breakpoint is hit, {expression} is evaluated"];

	private readonly IDebug _debugger;
	private readonly string _path;
	private readonly int _line;
	private readonly string[] _drafts = new string[3];
	private readonly TextEntry _entry = new() { FontSize = 12f };
	private BreakpointEditMode _mode;
	private float _anchorX;
	private float _anchorY;
	private float _width;
	private bool _modeHover;

	public BreakpointEditor(IDebug debugger, string path, int line, BreakpointEditMode mode) {
		_debugger = debugger;
		_path = path;
		_line = line;
		_mode = mode;
		Name = "BreakpointEditor";
		KeyboardInputEnabled = true;

		SourceBreakpoint? existing = debugger.FindBreakpoint(path, line);
		_drafts[0] = existing?.Condition ?? string.Empty;
		_drafts[1] = existing?.HitCondition ?? string.Empty;
		_drafts[2] = existing?.LogMessage ?? string.Empty;

		_entry.Submitted = Commit;
		AddChild(_entry);
		ShowMode();
	}

	public override bool DismissOnFocusLost => false;

	public void Open(float x, float y, float width) {
		_anchorX = x;
		_anchorY = y;
		_width = width;
		MakePopup();
		InvalidateLayout();
		_entry.RequestFocus();
	}

	public override void OnPopupDismissed() => ClosePopup();

	private void ShowMode() {
		_entry.Placeholder = Placeholders[(int)_mode];
		_entry.SetText(_drafts[(int)_mode]);
		Repaint();
	}

	private void CycleMode() {
		_drafts[(int)_mode] = _entry.GetString();
		_mode = (BreakpointEditMode)(((int)_mode + 1) % ModeLabels.Length);
		ShowMode();
		_entry.RequestFocus();
	}

	private void Commit() {
		_drafts[(int)_mode] = _entry.GetString();
		_debugger.UpdateBreakpoint(_path, _line, Value(0), Value(1), Value(2));
		ClosePopup();
	}

	private string? Value(int index) => _drafts[index].Trim().Length == 0 ? null : _drafts[index].Trim();

	public override void PerformLayout() {
		float scale = Scale;
		float height = Height * scale;

		SetBounds(_anchorX, _anchorY, _width, height);
		_entry.SetBounds((PaddingX + ModeWidth + PaddingX) * scale, 4f * scale, _width - ((PaddingX * 3f + ModeWidth) * scale), height - (8f * scale));
		base.PerformLayout();
	}

	private bool InMode(float x) => x >= PaddingX * Scale && x < (PaddingX + ModeWidth) * Scale;

	public override void OnCursorMoved(float x, float y) {
		bool hover = InMode(x);

		if (hover == _modeHover) return;

		_modeHover = hover;
		Repaint();
	}

	public override void OnCursorExited() {
		if (!_modeHover) return;

		_modeHover = false;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button == MouseButton.Left && InMode(x)) CycleMode();

		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (key != VirtualKey.Escape) return false;

		ClosePopup();
		return true;
	}

	public override void PaintBackground(IPainter painter) {
		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.EditorWidgetBackground);
		painter.FillRectangle(ScreenX, ScreenY, Wide, Scale, Theme.FocusBorder);
		painter.FillRectangle(ScreenX, ScreenY + Tall - Scale, Wide, Scale, Theme.FocusBorder);
	}

	public override void Paint(IPainter painter) {
		if (Ui is null) return;

		float scale = Scale;
		ITextFont font = Ui.GetFonts().GetUi(12f, false);
		float left = ScreenX + (PaddingX * scale);
		float top = ScreenY + (4f * scale);
		float height = Tall - (8f * scale);
		float middle = top + (height / 2f);

		painter.FillRoundedRectangle(left, top, ModeWidth * scale, height, Metrics.RadiusSmall * scale, _modeHover ? Theme.MenuSelectionBackground : Theme.CheckboxBackground);
		painter.StrokeRoundedRectangle(left, top, ModeWidth * scale, height, Metrics.RadiusSmall * scale, scale, Theme.CheckboxBorder);
		painter.DrawText(font, ModeLabels[(int)_mode], left + (6f * scale), middle - (font.GetLineHeight() / 2f) + font.GetBaseline(), Theme.MenuForeground);
		painter.DrawIcon(Codicon.ChevronDown, left + (ModeWidth * scale) - (10f * scale), middle, 12f * scale, Theme.MenuForeground);
	}
}
