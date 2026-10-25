using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;

namespace Forge.UI.Controls;

public enum FontRole : byte
{
	Ui,
	UiStrong,
	UiSmall,
	Code,
}

public enum TextAlignment : byte
{
	Left,
	Center,
	Right,
}

public class Label : Panel
{
	private const float IconSize = 14f;
	private const float IconGap = 4f;

	private static readonly Dictionary<string, Codicon> IconNames = BuildIconNames();

	private char[] _text = [];
	private int _length;

	public Label() => MouseInputEnabled = false;

	public Label(ReadOnlySpan<char> text) : this() => SetText(text);

	public ReadOnlySpan<char> Text => _text.AsSpan(0, _length);

	public FontRole Font { get; set; }

	public TextAlignment Alignment { get; set; }

	public Color Foreground { get; set; } = Theme.Foreground;

	public Func<Codicon, Color>? IconColor { get; set; }

	public Thickness Padding { get; set; } = Thickness.Zero;

	public bool SizeToContents { get; set; }

	public void SetText(ReadOnlySpan<char> text) {
		if (text.SequenceEqual(Text)) return;

		if (text.Length > _text.Length) _text = new char[Math.Max(text.Length, _text.Length * 2)];

		text.CopyTo(_text);
		_length = text.Length;

		if (SizeToContents) FitContents();

		Repaint();
	}

	public void FitContents() {
		if (Ui is null) return;

		float wide = MeasureContents(FontFor(Ui.GetFonts()), Scale) + ((Padding.Left + Padding.Right) * Scale);

		if (MathF.Abs(wide - Wide) < 0.5f) return;

		SetSize(wide, Tall);
		Parent?.InvalidateLayout();
	}

	protected ITextFont FontFor(IFontSet fonts) => Font switch {
		FontRole.UiStrong => fonts.GetUiStrong(),
		FontRole.UiSmall => fonts.GetUiSmall(),
		FontRole.Code => fonts.GetCode(),
		_ => fonts.GetUi(),
	};

	public float MeasureContents(ITextFont font, float scale) {
		float width = 0f;
		ReadOnlySpan<char> remaining = Text;

		while (Next(ref remaining, out ReadOnlySpan<char> run, out Codicon? icon)) width += icon is not null ? (IconSize + IconGap) * scale : font.Measure(run);

		return width;
	}

	public override void OnSizeChanged(float wide, float tall) {
		if (SizeToContents && wide <= 0f) FitContents();
	}

	public override void Paint(IPainter painter) => PaintContents(painter, Foreground);

	protected void PaintContents(IPainter painter, Color color) {
		if (_length == 0) return;

		float scale = Scale;
		ITextFont font = FontFor(Ui!.GetFonts());
		float left = ScreenX + (Padding.Left * scale);
		float right = ScreenX + Wide - (Padding.Right * scale);
		float width = MeasureContents(font, scale);

		float cursor = Alignment switch {
			TextAlignment.Center => left + MathF.Max((right - left - width) / 2f, 0f),
			TextAlignment.Right => MathF.Max(right - width, left),
			_ => left,
		};

		float centreY = ScreenY + (Tall / 2f);
		float baseline = ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();
		bool clip = width > right - left;

		if (clip) painter.PushClip(left, ScreenY, MathF.Max(right - left, 0f), Tall);

		ReadOnlySpan<char> remaining = Text;

		while (Next(ref remaining, out ReadOnlySpan<char> run, out Codicon? icon)) {
			if (icon is { } glyph) {
				painter.DrawIcon(glyph, cursor + (IconSize * scale / 2f), centreY, IconSize * scale, IconColor?.Invoke(glyph) ?? color);
				cursor += (IconSize + IconGap) * scale;
				continue;
			}

			painter.DrawText(font, run, cursor, baseline, color);
			cursor += font.Measure(run);
		}

		if (clip) painter.PopClip();
	}

	private static bool Next(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> run, out Codicon? icon) {
		icon = null;
		run = default;

		if (remaining.IsEmpty) return false;

		int start = remaining.IndexOf("$(");

		if (start != 0) {
			int length = start < 0 ? remaining.Length : start;
			run = remaining[..length];
			remaining = remaining[length..];
			return true;
		}

		int end = remaining.IndexOf(')');

		if (end < 0) {
			run = remaining;
			remaining = default;
			return true;
		}

		ReadOnlySpan<char> name = remaining[2..end];
		remaining = remaining[(end + 1)..];

		if (IconNames.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out Codicon codicon)) icon = codicon;
		else run = name;

		return true;
	}

	private static Dictionary<string, Codicon> BuildIconNames() {
		Dictionary<string, Codicon> names = new(StringComparer.OrdinalIgnoreCase);

		foreach (Codicon value in Enum.GetValues<Codicon>()) {
			string pascal = value.ToString();
			System.Text.StringBuilder kebab = new(pascal.Length + 4);

			for (int i = 0; i < pascal.Length; i++) {
				if (i > 0 && char.IsUpper(pascal[i])) kebab.Append('-');

				kebab.Append(char.ToLowerInvariant(pascal[i]));
			}

			names.TryAdd(kebab.ToString(), value);
			names.TryAdd(pascal, value);
		}

		return names;
	}
}
