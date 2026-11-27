using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core;

public sealed record PermissionRule(string Kind, string Target, bool Allowed);

public sealed class ExtensionPermissionSet
{
	public string Version { get; set; } = string.Empty;

	public List<PermissionRule> Rules { get; set; } = [];
}

public sealed class ExtensionPermissions
{
	private readonly string _path;
	private readonly Lock _sync = new();
	private Dictionary<string, ExtensionPermissionSet> _sets;

	public ExtensionPermissions(string path) {
		_path = path;
		_sets = Load(path);
	}

	public event Action<string>? Changed;

	public static string DefaultPath => Path.Combine(UserData.Directory, "extension-permissions.json");

	private static Dictionary<string, ExtensionPermissionSet> Load(string path) {
		try {
			if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), ExtensionPermissionsContext.Default.DictionaryStringExtensionPermissionSet) is { } loaded) return new Dictionary<string, ExtensionPermissionSet>(loaded, StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
		}

		return new Dictionary<string, ExtensionPermissionSet>(StringComparer.OrdinalIgnoreCase);
	}

	public IReadOnlyList<PermissionRule> Rules(string id, string version) {
		lock (_sync) return Current(id, version) is { } set ? [.. set.Rules] : [];
	}

	public bool Reconcile(string id, string version) {
		lock (_sync) {
			if (!_sets.TryGetValue(id, out ExtensionPermissionSet? set) || set.Version == version) return false;

			_sets.Remove(id);
			Save();
		}

		Changed?.Invoke(id);
		return true;
	}

	public void Set(string id, string version, string kind, string target, bool allowed) {
		lock (_sync) {
			ExtensionPermissionSet set = Current(id, version) ?? (_sets[id] = new ExtensionPermissionSet { Version = version });
			set.Rules.RemoveAll(rule => rule.Kind == kind && string.Equals(rule.Target, target, StringComparison.OrdinalIgnoreCase));
			set.Rules.Add(new PermissionRule(kind, target, allowed));
			Save();
		}

		Changed?.Invoke(id);
	}

	public void Remove(string id, string kind, string target) {
		lock (_sync) {
			if (!_sets.TryGetValue(id, out ExtensionPermissionSet? set)) return;

			set.Rules.RemoveAll(rule => rule.Kind == kind && string.Equals(rule.Target, target, StringComparison.OrdinalIgnoreCase));
			Save();
		}

		Changed?.Invoke(id);
	}

	public void Clear(string id) {
		lock (_sync) {
			if (!_sets.Remove(id)) return;

			Save();
		}

		Changed?.Invoke(id);
	}

	private ExtensionPermissionSet? Current(string id, string version) {
		if (!_sets.TryGetValue(id, out ExtensionPermissionSet? set)) return null;

		if (set.Version == version) return set;

		_sets.Remove(id);
		Save();
		return null;
	}

	private void Save() {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			string temporary = _path + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(_sets, ExtensionPermissionsContext.Default.DictionaryStringExtensionPermissionSet));
			File.Move(temporary, _path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, ExtensionPermissionSet>))]
internal partial class ExtensionPermissionsContext : JsonSerializerContext
{
}
