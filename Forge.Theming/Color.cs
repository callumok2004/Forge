using System.Globalization;

namespace Forge.Theming;

public readonly struct Color(float red, float green, float blue, float alpha = 1f)
{
	public readonly float R = red;
	public readonly float G = green;
	public readonly float B = blue;
	public readonly float A = alpha;

	public static Color Transparent => new(0f, 0f, 0f, 0f);

	public static bool TryParse(ReadOnlySpan<char> text, out Color color) {
		color = default;

		if (text.Length > 0 && text[0] == '#') text = text[1..];

		switch (text.Length) {
			case 3: {
					if (!TryNibble(text[0], out int r) || !TryNibble(text[1], out int g) || !TryNibble(text[2], out int b)) {
						return false;
					}

					color = FromBytes((byte)(r * 17), (byte)(g * 17), (byte)(b * 17), 255);
					return true;
				}

			case 6:
			case 8: {
					if (!byte.TryParse(text[..2], NumberStyles.HexNumber, null, out byte r) ||
						!byte.TryParse(text[2..4], NumberStyles.HexNumber, null, out byte g) ||
						!byte.TryParse(text[4..6], NumberStyles.HexNumber, null, out byte b)) return false;

					byte a = 255;
					if (text.Length == 8 && !byte.TryParse(text[6..8], NumberStyles.HexNumber, null, out a)) return false;

					color = FromBytes(r, g, b, a);
					return true;
				}

			default:
				return false;
		}
	}

	public static Color Parse(ReadOnlySpan<char> text) =>
		TryParse(text, out Color color) ? color : throw new FormatException($"'{text}' is not a colour.");

	public static Color FromBytes(byte r, byte g, byte b, byte a = 255) =>
		new(r / 255f, g / 255f, b / 255f, a / 255f);

	public uint ToArgb() => ((uint)MathF.Round(A * 255f) << 24) | ((uint)MathF.Round(R * 255f) << 16) | ((uint)MathF.Round(G * 255f) << 8) | (uint)MathF.Round(B * 255f);

	public Color Over(Color background) {
		if (A >= 1f) return new Color(R, G, B);

		float inverse = 1f - A;
		return new Color(
			(R * A) + (background.R * inverse),
			(G * A) + (background.G * inverse),
			(B * A) + (background.B * inverse));
	}

	public Color WithAlpha(float alpha) => new(R, G, B, alpha);

	public Color Transparency(float factor) => new(R, G, B, A * factor);

	public Color Lighten(float factor) => WithLightness(static (lightness, factor) => lightness + (lightness * factor), factor);

	public Color Darken(float factor) => WithLightness(static (lightness, factor) => lightness - (lightness * factor), factor);

	private Color WithLightness(Func<float, float, float> adjust, float factor) {
		float max = MathF.Max(R, MathF.Max(G, B));
		float min = MathF.Min(R, MathF.Min(G, B));
		float chroma = max - min;
		float lightness = (max + min) / 2f;
		float saturation = chroma == 0f ? 0f : chroma / (1f - MathF.Abs((2f * lightness) - 1f));
		float hue = 0f;

		if (chroma > 0f) {
			if (max == R) hue = ((G - B) / chroma) + (G < B ? 6f : 0f);
			else if (max == G) hue = ((B - R) / chroma) + 2f;
			else hue = ((R - G) / chroma) + 4f;

			hue *= 60f;
		}

		lightness = Math.Clamp(adjust(lightness, factor), 0f, 1f);
		saturation = Math.Clamp(saturation, 0f, 1f);

		float c = (1f - MathF.Abs((2f * lightness) - 1f)) * saturation;
		float x = c * (1f - MathF.Abs(((hue / 60f) % 2f) - 1f));
		float m = lightness - (c / 2f);

		(float r, float g, float b) = hue switch {
			< 60f => (c, x, 0f),
			< 120f => (x, c, 0f),
			< 180f => (0f, c, x),
			< 240f => (0f, x, c),
			< 300f => (x, 0f, c),
			_ => (c, 0f, x),
		};

		return new Color(r + m, g + m, b + m, A);
	}

	public override string ToString() =>
		$"#{(byte)(R * 255):X2}{(byte)(G * 255):X2}{(byte)(B * 255):X2}{(byte)(A * 255):X2}";

	private static bool TryNibble(char c, out int value) {
		value = c switch {
			>= '0' and <= '9' => c - '0',
			>= 'a' and <= 'f' => c - 'a' + 10,
			>= 'A' and <= 'F' => c - 'A' + 10,
			_ => -1,
		};

		return value >= 0;
	}
}
