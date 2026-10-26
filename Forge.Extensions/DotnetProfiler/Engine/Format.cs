using System.Globalization;

namespace Forge.Extensions.DotnetProfiler.Engine;

public static class Format
{
	private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

	public static string Bytes(double value) {
		double magnitude = Math.Abs(value);

		if (magnitude >= 1 << 30) return (value / (1 << 30)).ToString("0.00", Invariant) + " GB";

		if (magnitude >= 1 << 20) return (value / (1 << 20)).ToString("0.0", Invariant) + " MB";

		if (magnitude >= 1024) return (value / 1024).ToString("0", Invariant) + " KB";

		return value.ToString("0", Invariant) + " B";
	}

	public static string SignedBytes(double value) => (value >= 0 ? "+" : "−") + Bytes(Math.Abs(value));

	public static string Percent(double value) => value.ToString(value < 10 ? "0.0" : "0", Invariant) + "%";

	public static string Rate(double value) => Bytes(value) + "/s";

	public static string Count(double value) => value.ToString("0", Invariant);

	public static string PerSecond(double value) => value.ToString(value < 10 ? "0.0" : "0", Invariant) + "/s";

	public static string Number(long value) => value.ToString("N0", Invariant);

	public static string Signed(long value) => (value >= 0 ? "+" : "−") + Math.Abs(value).ToString("N0", Invariant);
}
