using Forge.Core;

namespace Forge.Common.Extensions;

public enum ExtensionKind
{
	BuiltIn,
	Installed,
	Marketplace,
}

public sealed record ExtensionContribution(string Kind, string Name, string? Detail = null);

public sealed record ExtensionEntry(string Id, string DisplayName, string Publisher, string Version, string Description, ExtensionKind Kind)
{
	public string? Directory { get; init; }

	public string? IconPath { get; init; }

	public string? ReadmePath { get; init; }

	public string? ChangelogPath { get; init; }

	public string? Repository { get; init; }

	public string? License { get; init; }

	public IReadOnlyList<ExtensionContribution> Contributions { get; init; } = [];

	public bool Verified { get; init; }

	public long Installs { get; init; }

	public double Rating { get; init; }

	public int RatingCount { get; init; }

	public DateTimeOffset? LastUpdated { get; init; }

	public string? Engine { get; init; }

	public IReadOnlyList<string> Categories { get; init; } = [];

	public Func<CancellationToken, Task<string?>>? LoadReadme { get; init; }

	public Func<CancellationToken, Task<string?>>? LoadChangelog { get; init; }

	public Func<CancellationToken, Task<string?>>? LoadManifest { get; init; }
}

public enum ExtensionOperation
{
	None,
	Checking,
	Installing,
	Uninstalling,
}

public interface IExtensionCatalog
{
	IReadOnlyList<ExtensionEntry> All();

	ExtensionEntry? Find(string id);

	bool IsEnabled(string id);

	bool IsGloballyEnabled(string id);

	bool? WorkspaceOverride(string id);

	bool HasWorkspace { get; }

	bool IsActive(string id);

	TimeSpan? ActivationTime(string id);

	long? HostMemory(string id);

	bool NeedsRestart(string id);

	void SetEnabled(string id, bool enabled, EnablementScope scope);

	Task<IReadOnlyList<ExtensionEntry>> SearchAsync(string query, CancellationToken cancellation);

	ExtensionEntry? AvailableUpdate(string id);

	ExtensionOperation Operation(string id);

	string? LastError(string id);

	bool IsCheckingAll { get; }

	Task CheckForUpdatesAsync(string? id);

	Task InstallAsync(ExtensionEntry entry);

	Task UninstallAsync(string id);

	IReadOnlyList<PermissionRule> Permissions(string id);

	void SetPermission(string id, string kind, string target, bool allowed);

	void RevokePermission(string id, string kind, string target);

	event Action? Changed;
}
