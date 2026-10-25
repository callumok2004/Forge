using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Language;

public sealed class RenameWidget : Panel
{
	private const float Width = 260f;
	private const float EntryHeight = 24f;
	private const float HintHeight = 20f;
	private const float Padding = 4f;
	private const string Hint = "Enter to Rename";

	private readonly TextEntry _entry = new() { Dock = DockType.Top };

	public RenameWidget() {
		Name = "Rename";
		Visible = false;
		ContextKey = "renameInputVisible";
		DockPadding = Thickness.Uniform(Padding);
		AddChild(_entry);
	}

	public bool IsOpen { get; private set; }

	public int Line { get; private set; }

	public int StartColumn { get; private set; }

	public Action<string>? Accepted { get; set; }

	public Action? Closed { get; set; }

	public void Show(int line, int startColumn, string name) {
		Line = line;
		StartColumn = startColumn;
		IsOpen = true;
		Visible = true;
		_entry.SetText(name);
		_entry.SelectAll();
		_entry.RequestFocus();
		InvalidateLayout();
		Repaint();
	}

	public void Hide() {
		if (!IsOpen) return;

		IsOpen = false;
		Visible = false;
		Repaint();
		Closed?.Invoke();
	}

	private bool HasFocusWithin() {
		for (Panel? panel = Ui?.GetFocus(); panel is not null; panel = panel.Parent) {
			if (panel == this) return true;
		}

		return false;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		switch (key) {
			case VirtualKey.Escape:
				Hide();
				return true;

			case VirtualKey.Return:
				string name = _entry.GetString();
				Hide();

				if (name.Length > 0) Accepted?.Invoke(name);

				return true;
		}

		return false;
	}

	public void Place(float x, float y, float lineHeight, float areaRight, float originX, float originY) {
		if (IsOpen && !HasFocusWithin()) Hide();

		Visible = IsOpen;

		if (!IsOpen) return;

		float scale = Scale;
		float width = Width * scale;
		float height = ((EntryHeight + HintHeight) * scale) + (Padding * 2f * scale);
		float left = Math.Max(0f, Math.Min(x, areaRight - width));

		_entry.SetSize(width - (Padding * 2f * scale), EntryHeight * scale);
		SetBounds(left - originX, y + lineHeight - originY, width, height);
	}

	public override void PaintBackground(IPainter painter) {
		float scale = Scale;
		float radius = Metrics.RadiusMedium * scale;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}

	public override void PostChildPaint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi();
		float top = ScreenY + ((Padding + EntryHeight) * scale);
		float baseline = top + (((HintHeight * scale) - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.DrawText(font, Hint, ScreenX + (Padding * 2f * scale), baseline, Theme.DescriptionForeground);
	}
}
