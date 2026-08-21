namespace Forge.Git;

public static class GitWorkspaceInfo
{
	public static (string Owner, string Repository)? ReadRemote(string rootPath)
	{
		try {
			string configPath = Path.Combine(rootPath, ".git", "config");
			if (!File.Exists(configPath)) return null;

			bool inOrigin = false;

			foreach (string raw in File.ReadLines(configPath)) {
				ReadOnlySpan<char> line = raw.AsSpan().Trim();

				if (line.StartsWith("[", StringComparison.Ordinal)) {
					inOrigin = line.StartsWith("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
					continue;
				}

				if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase)) continue;

				int equals = line.IndexOf('=');
				if (equals < 0) continue;

				return ParseRemote(line[(equals + 1)..].Trim());
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}

		return null;
	}

	public static (string Owner, string Repository)? ParseRemote(ReadOnlySpan<char> url)
	{
		url = url.TrimEnd('/');

		if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];

		int lastSlash = url.LastIndexOf('/');
		if (lastSlash <= 0) return null;

		ReadOnlySpan<char> repository = url[(lastSlash + 1)..];
		ReadOnlySpan<char> before = url[..lastSlash];

		int ownerStart = Math.Max(before.LastIndexOf('/'), before.LastIndexOf(':'));
		ReadOnlySpan<char> owner = before[(ownerStart + 1)..];

		return repository.IsEmpty || owner.IsEmpty ? null : (owner.ToString(), repository.ToString());
	}

	public static string? ReadBranchName(string rootPath)
	{
		try {
			string head = Path.Combine(rootPath, ".git", "HEAD");
			if (!File.Exists(head)) return null;

			ReadOnlySpan<char> text = File.ReadAllText(head).AsSpan().Trim();
			const string prefix = "ref: refs/heads/";

			return text.StartsWith(prefix, StringComparison.Ordinal)
				? text[prefix.Length..].ToString()
				: text[..Math.Min(7, text.Length)].ToString();
		}
		catch (IOException) {
			return null;
		}
		catch (UnauthorizedAccessException) {
			return null;
		}
	}
}
