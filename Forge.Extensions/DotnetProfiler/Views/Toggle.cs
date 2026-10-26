using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class Toggle : Panel
{
	private const float AnimationSeconds = 0.12f;

	private readonly string _label;
	private float _progress;

	public Toggle(string label) {
		_label = label;
		Name = label;
		Cursor = CursorCode.Hand;
	}

	public float FontSize { get; init; } = 12f;

	public bool Checked {
		get;
		set {
			if (field == value) return;

			field = value;

			if (Ui is null || !IsShowing) _progress = value ? 1f : 0f;
			else SetThinking(true);

			Repaint();
		}
	}

	public Action<bool>? Changed { get; set; }

	public float PreferredWidth(ITextFont font, float scale) => font.Measure(_label) + (19f * scale);

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() => Repaint();

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		Checked = !Checked;
		Changed?.Invoke(Checked);
		return true;
	}

	public override void OnThink(float deltaSeconds) {
		float target = Checked ? 1f : 0f;
		float step = deltaSeconds / AnimationSeconds;
		_progress = target > _progress ? MathF.Min(target, _progress + step) : MathF.Max(target, _progress - step);

		if (_progress == target) SetThinking(false);

		Repaint();
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float box = 14f * scale;
		float top = ScreenY + ((Tall - box) / 2f);
		ITextFont font = painter.GetUi(FontSize);
		float eased = 1f - ((1f - _progress) * (1f - _progress));

		painter.FillRoundedRectangle(ScreenX, top, box, box, 3f * scale, Theme.InputBackground);

		if (eased > 0f) painter.FillRoundedRectangle(ScreenX, top, box, box, 3f * scale, Theme.Accent.WithAlpha(eased));

		painter.StrokeRoundedRectangle(ScreenX, top, box, box, 3f * scale, Metrics.StrokeThickness * scale, IsCursorOver ? Theme.FocusBorder : eased > 0.5f ? Theme.Accent : Theme.InputBorder);

		if (eased > 0f) {
			float cx = ScreenX + (box / 2f);
			float cy = top + (box / 2f);
			float size = (0.6f + (0.4f * eased)) * scale;
			Color check = Theme.ButtonForeground.WithAlpha(eased);
			painter.StrokeLine(cx - (3.5f * size), cy, cx - (1f * size), cy + (2.5f * size), 1.6f * scale, check);
			painter.StrokeLine(cx - (1f * size), cy + (2.5f * size), cx + (4f * size), cy - (3f * size), 1.6f * scale, check);
		}

		painter.DrawText(font, _label, ScreenX + box + (5f * scale), ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline(), Theme.EditorForeground.WithAlpha(0.85f));
	}
}
