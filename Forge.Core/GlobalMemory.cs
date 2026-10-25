using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core;

public sealed class GlobalMemory
{
	private const int MaxRecentFolders = 50;

	[JsonIgnore]
	private string _path = string.Empty;

	public List<string> RecentCommands { get; set; } = [];

	public List<string> RecentFolders { get; set; } = [];

	public List<string> RecentFiles { get; set; } = [];

	public float PeekRatio { get; set; } = 0.7f;

	public int PeekHeightInLines { get; set; } = 18;

	public static GlobalMemory Load(string path) {
		try {
			if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), GlobalMemoryContext.Default.GlobalMemory) is { } loaded) {
				loaded._path = path;
				return loaded;
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
		}

		return new GlobalMemory { _path = path };
	}

	public static void Touch(List<string> list, string value, int limit) {
		int existing = list.FindIndex(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

		if (existing >= 0) list.RemoveAt(existing);

		list.Insert(0, value);

		if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
	}

	public void TouchFolder(string folder) => Touch(RecentFolders, folder, MaxRecentFolders);

	public void TouchFile(string file) => Touch(RecentFiles, file, MaxRecentFolders);

	public void Save() {
		if (_path.Length == 0) return;

		try {
			string temporary = _path + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(this, GlobalMemoryContext.Default.GlobalMemory));
			File.Move(temporary, _path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
		}
	}
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GlobalMemory))]
internal partial class GlobalMemoryContext : JsonSerializerContext
{
}
