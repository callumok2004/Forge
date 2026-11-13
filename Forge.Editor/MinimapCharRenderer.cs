namespace Forge.Editor;

internal readonly record struct Rgba8(byte R, byte G, byte B, byte A)
{
	public static Rgba8 FromPacked(uint packed) => new((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed, (byte)(packed >> 24));

	public static Rgba8 From(Theming.Color color) => new(ToByte(color.R * 255f), ToByte(color.G * 255f), ToByte(color.B * 255f), ToByte(MathF.Round(color.A * 255f)));

	private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
}

internal sealed class MinimapCharRenderer
{
	public const int StartCharCode = 32;
	public const int EndCharCode = 126;
	public const int UnknownCode = 65533;
	public const int CharCount = EndCharCode - StartCharCode + 2;
	public const int SampledCharHeight = 16;
	public const int SampledCharWidth = 10;
	public const int BaseCharHeight = 2;
	public const int BaseCharWidth = 1;

	private static MinimapCharRenderer? _lastCreated;
	private static string? _lastFontFamily;

	private readonly byte[] _charDataNormal;
	private readonly byte[] _charDataLight;

	public int Scale { get; }

	private MinimapCharRenderer(byte[] charData, int scale) {
		Scale = scale;
		_charDataNormal = Soften(charData, 12f / 15f);
		_charDataLight = Soften(charData, 50f / 60f);
	}

	public static MinimapCharRenderer Create(int scale, string fontFamily, Func<byte[]?> sample) {
		if (_lastCreated is { } last && last.Scale == scale && fontFamily == _lastFontFamily) return last;

		MinimapCharRenderer renderer = scale switch {
			1 => new MinimapCharRenderer(Decode(MinimapPreBaked.Scale1), 1),
			2 => new MinimapCharRenderer(Decode(MinimapPreBaked.Scale2), 2),
			_ => new MinimapCharRenderer(Downsample(sample() ?? new byte[CharCount * SampledCharWidth * SampledCharHeight], scale), scale),
		};

		_lastFontFamily = fontFamily;
		_lastCreated = renderer;
		return renderer;
	}

	public static string SampleCharacters() {
		char[] characters = new char[CharCount];

		for (int i = StartCharCode; i <= EndCharCode; i++) characters[i - StartCharCode] = (char)i;

		characters[^1] = (char)UnknownCode;
		return new string(characters);
	}

	private static byte[] Decode(string text) {
		byte[] output = new byte[text.Length / 2];

		for (int i = 0; i < text.Length; i += 2) output[i >> 1] = (byte)((Hex(text[i]) << 4) | (Hex(text[i + 1]) & 0xF));

		return output;
	}

	private static int Hex(char c) => c <= '9' ? c - '0' : c - 'A' + 10;

	private static byte ToUint8(double value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)(int)value;

	private static byte Clamped(double value) => double.IsNaN(value) ? (byte)0 : (byte)Math.Clamp(Math.Round(value, MidpointRounding.ToEven), 0, 255);

	private static byte[] Soften(byte[] input, double ratio) {
		byte[] result = new byte[input.Length];

		for (int i = 0; i < input.Length; i++) result[i] = ToUint8(input[i] * ratio);

		return result;
	}

	private static byte[] Downsample(byte[] coverage, int scale) {
		int pixelsPerCharacter = BaseCharHeight * scale * BaseCharWidth * scale;
		byte[] result = new byte[pixelsPerCharacter * CharCount];
		int resultOffset = 0;
		int sourceOffset = 0;
		double brightest = 0;

		for (int charIndex = 0; charIndex < CharCount; charIndex++) {
			brightest = Math.Max(brightest, DownsampleChar(coverage, sourceOffset, result, resultOffset, scale));
			resultOffset += pixelsPerCharacter;
			sourceOffset += SampledCharWidth;
		}

		if (brightest > 0) {
			double adjust = 255 / brightest;

			for (int i = 0; i < result.Length; i++) result[i] = Clamped(result[i] * adjust);
		}

		return result;
	}

	private static double DownsampleChar(byte[] source, int sourceOffset, byte[] dest, int destOffset, int scale) {
		int width = BaseCharWidth * scale;
		int height = BaseCharHeight * scale;
		int rowWidth = CharCount * SampledCharWidth;
		int targetIndex = destOffset;
		double brightest = 0;

		for (int y = 0; y < height; y++) {
			double sourceY1 = (double)y / height * SampledCharHeight;
			double sourceY2 = (double)(y + 1) / height * SampledCharHeight;

			for (int x = 0; x < width; x++) {
				double sourceX1 = (double)x / width * SampledCharWidth;
				double sourceX2 = (double)(x + 1) / width * SampledCharWidth;
				double value = 0;
				double samples = 0;

				for (double sy = sourceY1; sy < sourceY2; sy++) {
					int sourceRow = sourceOffset + ((int)Math.Floor(sy) * rowWidth);
					double yBalance = 1 - (sy - Math.Floor(sy));

					for (double sx = sourceX1; sx < sourceX2; sx++) {
						double xBalance = 1 - (sx - Math.Floor(sx));
						int sourceIndex = sourceRow + (int)Math.Floor(sx);
						double weight = xBalance * yBalance;
						samples += weight;
						value += source[sourceIndex] * weight;
					}
				}

				double final = value / samples;
				brightest = Math.Max(brightest, final);
				dest[targetIndex++] = ToUint8(final);
			}
		}

		return brightest;
	}

	private static int CharIndex(int charCode, int fontScale) {
		charCode -= StartCharCode;

		if (charCode < 0 || charCode > CharCount) return fontScale <= 2 ? (charCode + CharCount) % CharCount : CharCount - 1;

		return charCode;
	}

	public void RenderChar(uint[] target, int targetWidth, int targetHeight, int dx, int dy, int charCode, Rgba8 color, int foregroundAlpha, Rgba8 background, int backgroundAlpha, int fontScale, bool useLighterFont, bool force1pxHeight) {
		int charWidth = BaseCharWidth * Scale;
		int charHeight = BaseCharHeight * Scale;
		int renderHeight = force1pxHeight ? 1 : charHeight;

		if (dx + charWidth > targetWidth || dy + renderHeight > targetHeight) return;

		byte[] charData = useLighterFont ? _charDataLight : _charDataNormal;
		int charIndex = CharIndex(charCode, fontScale);
		int deltaR = color.R - background.R;
		int deltaG = color.G - background.G;
		int deltaB = color.B - background.B;
		int destAlpha = Math.Max(foregroundAlpha, backgroundAlpha);
		int sourceOffset = charIndex * charWidth * charHeight;
		int row = (dy * targetWidth) + dx;

		for (int y = 0; y < renderHeight; y++) {
			int column = row;

			for (int x = 0; x < charWidth; x++) {
				double c = sourceOffset < charData.Length ? charData[sourceOffset] / 255.0 * (foregroundAlpha / 255.0) : double.NaN;
				sourceOffset++;
				target[column++] = Pixel(background.R + (deltaR * c), background.G + (deltaG * c), background.B + (deltaB * c), destAlpha);
			}

			row += targetWidth;
		}
	}

	public void BlockRenderChar(uint[] target, int targetWidth, int targetHeight, int dx, int dy, Rgba8 color, int foregroundAlpha, Rgba8 background, int backgroundAlpha, bool force1pxHeight) {
		int charWidth = BaseCharWidth * Scale;
		int charHeight = BaseCharHeight * Scale;
		int renderHeight = force1pxHeight ? 1 : charHeight;

		if (dx + charWidth > targetWidth || dy + renderHeight > targetHeight) return;

		double c = 0.5 * (foregroundAlpha / 255.0);
		uint pixel = Pixel(background.R + ((color.R - background.R) * c), background.G + ((color.G - background.G) * c), background.B + ((color.B - background.B) * c), Math.Max(foregroundAlpha, backgroundAlpha));
		int row = (dy * targetWidth) + dx;

		for (int y = 0; y < renderHeight; y++) {
			target.AsSpan(row, charWidth).Fill(pixel);
			row += targetWidth;
		}
	}

	public static uint Pixel(double r, double g, double b, int a) {
		byte red = Clamped(r);
		byte green = Clamped(g);
		byte blue = Clamped(b);
		byte alpha = (byte)Math.Clamp(a, 0, 255);

		if (alpha != 255) {
			red = (byte)((red * alpha) / 255);
			green = (byte)((green * alpha) / 255);
			blue = (byte)((blue * alpha) / 255);
		}

		return ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
	}
}
