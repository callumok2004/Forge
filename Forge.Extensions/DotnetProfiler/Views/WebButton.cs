using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public enum ButtonStyle : byte
{
	Secondary,
	Primary,
	Recording,
}

public sealed class WebButton : Panel
{
	private static readonly Color RecordingBackground = Color.Parse("#c93c37");
	private static readonly Color RecordingHover = Color.Parse("#d4504b");

	private string _text;

	public WebButton(string text, Action action, ButtonStyle style = ButtonStyle.Secondary) {
		_text = text;
		Action = action;
		Style = style;
		Name = text;
		Cursor = CursorCode.Hand;
	}

	public Action Action { get; set; }

	public ButtonStyle Style {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public float FontSize { get; init; } = 12f;

	public float PaddingX { get; init; } = 12f;

	public string Text {
		get => _text;
		set {
			if (_text == value) return;

			_text = value;
			Parent?.InvalidateLayout();
			Repaint();
		}
	}

	public float PreferredWidth(float scale) => Ui is null ? 80f * scale : Ui.GetFonts().GetUi(FontSize).Measure(_text) + (PaddingX * 2f * scale);

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() => Repaint();

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return false;

		if (Enabled) Action();

		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		bool hover = Enabled && IsCursorOver;
		ITextFont font = painter.GetUi(FontSize);

		(Color background, Color foreground) = Style switch {
			ButtonStyle.Primary => (hover ? Theme.ButtonHoverBackground : Theme.ButtonBackground, Theme.ButtonForeground),
			ButtonStyle.Recording => (hover ? RecordingHover : RecordingBackground, Theme.ButtonForeground),
			_ => (hover ? Theme.ButtonSecondaryHoverBackground : Theme.ButtonSecondaryBackground, Theme.ButtonSecondaryForeground),
		};

		if (!Enabled) {
			background = background.WithAlpha(0.5f);
			foreground = foreground.WithAlpha(0.5f);
		}

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, 3f * scale, background);
		painter.DrawText(font, _text, ScreenX + ((Wide - font.Measure(_text)) / 2f), ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline(), foreground);
	}
}

public sealed class StatusPill : Panel
{
	private static readonly Color LiveDot = Color.Parse("#3fb950");

	private string _text = "no session";
	private bool _live;

	public StatusPill() {
		Name = "Status";
		MouseInputEnabled = false;
	}

	public void Set(string text, bool live) {
		if (_text == text && _live == live) return;

		_text = text;
		_live = live;
		Parent?.InvalidateLayout();
		Repaint();
	}

	public float PreferredWidth(float scale) => Ui is null ? 100f * scale : Ui.GetFonts().GetUi(12f).Measure((_live ? "● " : "○ ") + _text) + (16f * scale);

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi(12f);
		float baseline = ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();
		string dot = _live ? "● " : "○ ";

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, MathF.Min(10f * scale, Tall / 2f), Theme.EditorForeground.WithAlpha(0.15f * 0.85f));
		painter.DrawText(font, dot, ScreenX + (8f * scale), baseline, _live ? LiveDot : Theme.EditorForeground.WithAlpha(0.5f));
		painter.DrawText(font, _text, ScreenX + (8f * scale) + font.Measure(dot), baseline, Theme.EditorForeground.WithAlpha(0.85f));
	}
}
