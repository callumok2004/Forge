using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Language;

public sealed class EditorMessageWidget : Panel
{
	private const float Padding = 6f;
	private const int VisibleMilliseconds = 3000;

	private string _text = string.Empty;
	private long _hideAt;

	public EditorMessageWidget() {
		Name = "EditorMessage";
		Visible = false;
		MouseInputEnabled = false;
	}

	public bool IsOpen { get; private set; }

	public int Offset { get; private set; }

	public void Show(string text, int offset) {
		_text = text;
		Offset = offset;
		IsOpen = true;
		_hideAt = Environment.TickCount64 + VisibleMilliseconds;
		AddTickSignal(250);
		InvalidateLayout();
		Repaint();
	}

	public void Hide() {
		if (!IsOpen) return;

		IsOpen = false;
		Visible = false;
		RemoveTickSignal();
		Repaint();
	}

	public override void OnTick() {
		if (Environment.TickCount64 >= _hideAt) Hide();
	}

	public void Place(float x, float y, float areaRight, float originX, float originY) {
		Visible = IsOpen;

		if (!IsOpen || Ui is null) return;

		float scale = Scale;
		ITextFont font = Ui.GetFonts().GetUi();
		float width = font.Measure(_text) + (Padding * 2f * scale);
		float height = font.GetLineHeight() + (Padding * 2f * scale);
		float left = Math.Max(0f, Math.Min(x, areaRight - width));

		SetBounds(left - originX, y - height - originY, width, height);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi();
		float radius = Metrics.RadiusSmall * scale;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.InfoForeground);
		painter.DrawText(font, _text, ScreenX + (Padding * scale), ScreenY + (Padding * scale) + font.GetBaseline(), Theme.EditorForeground);
	}
}
