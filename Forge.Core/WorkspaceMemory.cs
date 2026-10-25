using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core;

public sealed class WorkspaceMemory
{
	private const int MaxFiles = 64;

	[JsonIgnore]
	private string _path = string.Empty;

	public string Root { get; set; } = string.Empty;

	public List<string> OpenFiles { get; set; } = [];

	public string? ActiveFile { get; set; }

	public string? SidebarView { get; set; }

	public bool ShowSidebar { get; set; } = true;

	public float SidebarWidth { get; set; }

	public bool PanelOpen { get; set; }

	public string? PanelView { get; set; }

	public float PanelHeight { get; set; }

	public bool AuxiliaryOpen { get; set; }

	public float AuxiliaryWidth { get; set; }

	public string? AuxiliaryView { get; set; }

	public string? DebugConfiguration { get; set; }

	public List<string> ExpandedFolders { get; set; } = [];

	public Dictionary<string, EditorViewState> ViewStates { get; set; } = [];

	public List<string> QuickOpenHistory { get; set; } = [];

	public List<string> CollapsedSections { get; set; } = [];

	public static WorkspaceMemory Load(string root) {
		string path = PathFor(root);

		try {
			if (File.Exists(path)) {
				WorkspaceMemory? loaded = JsonSerializer.Deserialize(File.ReadAllText(path),
					WorkspaceMemoryContext.Default.WorkspaceMemory);

				if (loaded is not null) {
					loaded._path = path;
					loaded.Root = root;
					return loaded;
				}
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException
			or UnauthorizedAccessException) {
		}

		return new WorkspaceMemory { _path = path, Root = root };
	}

	public void Save() {
		if (_path.Length == 0) return;

		if (OpenFiles.Count > MaxFiles) OpenFiles.RemoveRange(MaxFiles, OpenFiles.Count - MaxFiles);

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

			string json = JsonSerializer.Serialize(this, WorkspaceMemoryContext.Default.WorkspaceMemory);

			string temporary = _path + ".tmp";
			File.WriteAllText(temporary, json);
			File.Move(temporary, _path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or NotSupportedException) {
		}
	}

	private static string PathFor(string root) {
		string normalised = root.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
		string name = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();

		return Path.Combine(UserData.WorkspacesDirectory, name + ".json");
	}
}

public sealed record EditorViewState(int Anchor, int Caret, float ScrollTop);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WorkspaceMemory))]
internal partial class WorkspaceMemoryContext : JsonSerializerContext
{
}
