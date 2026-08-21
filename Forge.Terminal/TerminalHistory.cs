using System.Text.Json.Serialization;
using System.Text.Json;

namespace Forge.Terminal;

public sealed class TerminalHistory
{
	private const int Limit = 100;

	private readonly List<string> _entries = [];
	private readonly string _path;

	public TerminalHistory(string path) {
		_path = path;
		Load();
	}

	public IReadOnlyList<string> Entries => _entries;

	public void Add(string command) {
		command = command.Trim();

		if (command.Length == 0) return;

		_entries.Remove(command);
		_entries.Add(command);

		Trim();
		Save();
	}

	public string? Suggest(string prefix) {
		if (prefix.Length == 0) return null;

		for (int i = _entries.Count - 1; i >= 0; i--) {
			string entry = _entries[i];

			if (entry.Length > prefix.Length && entry.StartsWith(prefix, StringComparison.Ordinal)) return entry;
		}

		return null;
	}

	private void Trim() {
		if (_entries.Count > Limit) _entries.RemoveRange(0, _entries.Count - Limit);
	}

	private void Load() {
		try {
			if (File.Exists(_path)) {
				string[]? stored = JsonSerializer.Deserialize(File.ReadAllText(_path),
					HistoryContext.Default.StringArray);

				if (stored is not null) _entries.AddRange(stored);
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException
			or UnauthorizedAccessException) {
		}

		foreach (string command in ReadPowerShellHistory()) {
			if (!_entries.Contains(command)) _entries.Insert(0, command);
		}

		Trim();
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

			string json = JsonSerializer.Serialize([.. _entries], HistoryContext.Default.StringArray);

			string temporary = _path + ".tmp";
			File.WriteAllText(temporary, json);
			File.Move(temporary, _path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or NotSupportedException) {
		}
	}

	public static IReadOnlyList<string> ReadPowerShellHistory() {
		string path = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt");

		if (!File.Exists(path)) return [];

		try {
			List<string> commands = [];
			string? pending = null;

			foreach (string line in File.ReadLines(path)) {
				pending = pending is null ? line : $"{pending}\n{line}";

				if (line.EndsWith('`')) continue;

				string command = pending.Trim();
				pending = null;

				if (command.Length > 0 && !command.Contains('\n')) commands.Add(command);
			}

			return commands;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return [];
		}
	}
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(string[]))]
internal partial class HistoryContext : JsonSerializerContext
{
}
