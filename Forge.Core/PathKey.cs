namespace Forge.Core;

public static class PathKey
{
	public static string Normalize(string path) {
		if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal)) return path;

		try {
			return Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
			return path;
		}
	}

	public static bool Same(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
