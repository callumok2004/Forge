namespace Forge.Core;

public enum EnablementScope
{
	Global,
	Workspace,
}

public sealed class ExtensionEnablement(GlobalMemory global, WorkspaceMemory? workspace)
{
	public event Action<string>? Changed;

	public bool IsGloballyEnabled(string id) => !global.DisabledExtensions.Exists(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));

	public bool? WorkspaceOverride(string id) => workspace is not null && workspace.ExtensionOverrides.TryGetValue(id.ToLowerInvariant(), out bool value) ? value : null;

	public bool IsEnabled(string id) => WorkspaceOverride(id) ?? IsGloballyEnabled(id);

	public void Set(string id, bool enabled, EnablementScope scope) {
		if (scope == EnablementScope.Workspace && workspace is not null) {
			string key = id.ToLowerInvariant();

			if (enabled == IsGloballyEnabled(id)) workspace.ExtensionOverrides.Remove(key);
			else workspace.ExtensionOverrides[key] = enabled;

			workspace.Save();
		}
		else {
			global.DisabledExtensions.RemoveAll(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));

			if (!enabled) global.DisabledExtensions.Add(id);

			workspace?.ExtensionOverrides.Remove(id.ToLowerInvariant());
			global.Save();
			workspace?.Save();
		}

		Changed?.Invoke(id);
	}
}
