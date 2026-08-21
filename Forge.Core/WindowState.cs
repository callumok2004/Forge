using System.Text.Json.Serialization;
using System.Text.Json;

namespace Forge.Core;

public sealed class WindowState
{
	private const int MinimumSize = 240;

	public int X { get; set; }

	public int Y { get; set; }

	public int Width { get; set; }

	public int Height { get; set; }

	public bool IsMaximized { get; set; }

	[JsonIgnore]
	public bool HasPosition => Width >= MinimumSize && Height >= MinimumSize;

	public static WindowState Load(string path) {
		try {
			if (File.Exists(path)) {
				return JsonSerializer.Deserialize(File.ReadAllText(path), StateContext.Default.WindowState)
					?? new WindowState();
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException
			or UnauthorizedAccessException) {
		}

		return new WindowState();
	}

	public void Save(string path) {
		if (!HasPosition) return;

		try {
			string json = JsonSerializer.Serialize(this, StateContext.Default.WindowState);

			string temporary = path + ".tmp";
			File.WriteAllText(temporary, json);
			File.Move(temporary, path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or NotSupportedException) {
		}
	}
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowState))]
internal partial class StateContext : JsonSerializerContext
{
}
