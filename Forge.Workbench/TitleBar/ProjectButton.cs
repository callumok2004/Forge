using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ProjectButton : Button
{
	private const float TileSize = 20f;
	private const float Gap = 6f;

	private static readonly Color White = Color.Parse("#ffffff");

	private string? _name;
	private string _initials = string.Empty;
	private Color _tile;

	public ProjectButton() {
		Name = "Project";
		Font = FontRole.UiStrong;
		Foreground = Theme.TitleBarForeground;
		Radius = Metrics.RadiusMedium;
		Padding = new Thickness(Gap + TileSize + Gap, 0f, Gap, 0f);
		IconColor = _ => Theme.TitleBarForeground.WithAlpha(0.55f);
		SizeToContents = true;
	}

	public void SetProject(string? name) {
		if (_name == name) return;

		_name = name;
		Visible = name is { Length: > 0 };

		if (!Visible) return;

		_tile = NameColour(name!);
		_initials = Initials(name!);
		SetText($"{name} $(chevron-down)");
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float tile = TileSize * scale;
		float left = ScreenX + (Gap * scale);
		float top = ScreenY + ((Tall - tile) / 2f);
		float radius = Metrics.RadiusMedium * scale;

		painter.FillRoundedRectangle(left, top, tile, tile, radius, _tile);
		painter.StrokeRoundedRectangle(left, top, tile, tile, radius, Metrics.StrokeThickness * scale, Theme.TitleBarForeground.WithAlpha(0.1f));

		ITextFont small = painter.GetUiSmall();
		painter.DrawText(small, _initials, left + ((tile - small.Measure(_initials)) / 2f), ScreenY + ((Tall - small.GetLineHeight()) / 2f) + small.GetBaseline(), White);

		base.Paint(painter);
	}

	private static string Initials(string name) {
		Span<char> letters = stackalloc char[2];
		int count = 0;
		bool atStart = true;

		for (int i = 0; i < name.Length && count < 2; i++) {
			char character = name[i];

			if (char.IsWhiteSpace(character) || character is '.' or '_' or '-') {
				atStart = true;
				continue;
			}

			bool camel = i > 0 && char.IsLower(name[i - 1]) && char.IsUpper(character);

			if (atStart || camel) letters[count++] = char.ToUpperInvariant(character);

			atStart = false;
		}

		if (count < 2) return name[..Math.Min(2, name.Length)].ToUpperInvariant();

		return new string(letters);
	}

	private static Color NameColour(string name) {
		int hash = 0;

		foreach (char character in name) hash = unchecked((hash * 31) + character);

		return FromHsl(((hash % 360) + 360) % 360, 0.6f, 0.55f);
	}

	private static Color FromHsl(float hue, float saturation, float lightness) {
		float a = saturation * Math.Min(lightness, 1f - lightness);

		float Channel(float n) {
			float k = (n + (hue / 30f)) % 12f;
			return lightness - (a * Math.Max(-1f, Math.Min(Math.Min(k - 3f, 9f - k), 1f)));
		}

		return new Color(Channel(0f), Channel(8f), Channel(4f));
	}
}
