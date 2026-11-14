using Forge.Theming;

namespace Forge.Tests;

public class ColorAdjustTests
{
	private static void Near(string expected, Color actual) {
		Color wanted = Color.Parse(expected);

		Assert.InRange(MathF.Abs(wanted.R - actual.R) * 255f, 0f, 1.01f);
		Assert.InRange(MathF.Abs(wanted.G - actual.G) * 255f, 0f, 1.01f);
		Assert.InRange(MathF.Abs(wanted.B - actual.B) * 255f, 0f, 1.01f);
		Assert.InRange(MathF.Abs(wanted.A - actual.A) * 255f, 0f, 1.01f);
	}

	[Fact]
	public void LightenRaisesLightness() => Near("#2c2c2d", Color.Parse("#252526").Lighten(0.2f));

	[Fact]
	public void DarkenLowersLightness() => Near("#e7e7e7", Color.Parse("#f3f3f3").Darken(0.05f));

	[Fact]
	public void LightenKeepsHue() {
		Color lighter = Color.Parse("#3366cc").Lighten(0.2f);

		Assert.True(lighter.B > lighter.G && lighter.G > lighter.R);
		Assert.True(lighter.B > Color.Parse("#3366cc").B);
	}

	[Fact]
	public void LightenClampsAtWhite() => Near("#ffffff", Color.Parse("#eeeeee").Lighten(1f));

	[Fact]
	public void LightenKeepsAlpha() => Assert.Equal(0.5f, Color.Parse("#25252680").Lighten(0.2f).A, 0.01f);

	[Fact]
	public void TransparencyMultipliesAlpha() => Assert.Equal(0.25f, Color.Parse("#ffffff80").Transparency(0.5f).A, 0.01f);

	[Fact]
	public void GreyStaysGrey() {
		Color result = Color.Parse("#808080").Lighten(0.1f);

		Assert.Equal(result.R, result.G, 0.001f);
		Assert.Equal(result.G, result.B, 0.001f);
	}
}
