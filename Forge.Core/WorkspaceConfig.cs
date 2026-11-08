namespace Forge.Core;

public static class WorkspaceConfig
{
	public const string ForgeFolder = ".forge";
	public const string VSCodeFolder = ".vscode";

	public static string PathFor(string workspaceFolder, string fileName) {
		string forge = Path.Combine(workspaceFolder, ForgeFolder, fileName);

		if (File.Exists(forge)) return forge;

		return Path.Combine(workspaceFolder, VSCodeFolder, fileName);
	}

	public static IEnumerable<string> Layers(string workspaceFolder, string fileName) {
		string vscode = Path.Combine(workspaceFolder, VSCodeFolder, fileName);

		if (File.Exists(vscode)) yield return vscode;

		string forge = Path.Combine(workspaceFolder, ForgeFolder, fileName);

		if (File.Exists(forge)) yield return forge;
	}

	public static bool IsConfigFolder(ReadOnlySpan<char> directory) {
		ReadOnlySpan<char> name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
		return name.Equals(ForgeFolder, StringComparison.OrdinalIgnoreCase) || name.Equals(VSCodeFolder, StringComparison.OrdinalIgnoreCase);
	}
}
