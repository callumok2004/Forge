using System.Reflection;

using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;
using Forge.VSCode;
using Forge.VSCode.Gallery;

namespace Forge.Shell;

public sealed class ExtensionCatalog : IExtensionCatalog
{
	private static readonly string AppVersion = typeof(ExtensionCatalog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

	private readonly ExtensionEnablement _enablement;
	private readonly ExtensionHost _host;
	private readonly ICommandHost _commandHost;
	private readonly Action<Action> _post;
	private readonly HashSet<string> _restart = [with(StringComparer.OrdinalIgnoreCase)];

	private static readonly string IconCache = Path.Combine(Path.GetTempPath(), "forge-gallery-icons");

	private readonly Dictionary<string, ExtensionEntry> _remote = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, GalleryExtension> _gallery = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, ExtensionEntry> _updates = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, ExtensionOperation> _operations = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, string> _errors = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _sync = new();

	private HashSet<string>? _malicious;
	private VSCodeBridge? _bridge;
	private List<ExtensionEntry> _entries = [];

	public ExtensionCatalog(ExtensionEnablement enablement, ExtensionHost host, ICommandHost commandHost, Action<Action> post, bool hasWorkspace) {
		_post = post;
		_enablement = enablement;
		_host = host;
		_commandHost = commandHost;
		HasWorkspace = hasWorkspace;

		try {
			if (Directory.Exists(IconCache)) Directory.Delete(IconCache, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}

		Rebuild();
	}

	public event Action? Changed;

	public bool HasWorkspace { get; }

	public VSCodeBridge? Bridge {
		get => _bridge;
		set {
			_bridge = value;

			if (value is not null) value.ExtensionsChanged = Rebuild;

			Rebuild();
		}
	}

	public void Rebuild() {
		List<ExtensionEntry> entries = [];

		foreach (IForgeExtension extension in _host.All()) {
			string id = extension.GetIdentifier();
			List<ExtensionContribution> contributions = [.. _host.CommandsOf(id).Where(static command => !command.GetId().EndsWith(".activate", StringComparison.Ordinal) && !command.GetId().EndsWith(".deactivate", StringComparison.Ordinal) && !command.GetId().EndsWith(".reload", StringComparison.Ordinal)).Select(static command => new ExtensionContribution("Commands", command.GetLabel(), command.GetId()))];

			entries.Add(new ExtensionEntry(id, extension.GetDisplayName(), "Forge", AppVersion, extension.GetDescription(), ExtensionKind.BuiltIn) { Contributions = contributions });
		}

		foreach (VSCodeExtension extension in _bridge?.Discovered ?? []) {
			List<ExtensionContribution> contributions = [.. extension.Commands.Select(static command => new ExtensionContribution("Commands", command.Category is { Length: > 0 } category ? $"{category}: {command.Title}" : command.Title, command.Id))];

			foreach (VSCodeViewContainer container in extension.Containers) {
				foreach (VSCodeWebviewView view in container.WebviewViews) contributions.Add(new ExtensionContribution("Views", view.Name.Length > 0 ? view.Name : container.Title, view.Id));
			}

			entries.Add(new ExtensionEntry(extension.Identifier, extension.DisplayName, extension.Publisher, extension.Version, extension.Description, ExtensionKind.Installed) {
				Directory = extension.Directory,
				IconPath = extension.Icon is { } icon && File.Exists(icon) ? icon : null,
				ReadmePath = FindFile(extension.Directory, "README.md"),
				ChangelogPath = FindFile(extension.Directory, "CHANGELOG.md"),
				Repository = extension.Repository,
				License = extension.License,
				Contributions = contributions,
			});
		}

		_entries = entries;
		Raise();
	}

	private void Raise() => _post(() => Changed?.Invoke());

	private static string? FindFile(string directory, string name) {
		try {
			foreach (string file in Directory.EnumerateFiles(directory)) {
				if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)) return file;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}

		return null;
	}

	public IReadOnlyList<ExtensionEntry> All() => _entries;

	public ExtensionEntry? Find(string id) {
		if (_entries.Find(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)) is { } local) return local;

		lock (_sync) return _remote.TryGetValue(id, out ExtensionEntry? remote) ? remote : null;
	}

	public bool IsEnabled(string id) => _enablement.IsEnabled(id);

	public bool IsGloballyEnabled(string id) => _enablement.IsGloballyEnabled(id);

	public bool? WorkspaceOverride(string id) => _enablement.WorkspaceOverride(id);

	public bool IsActive(string id) => Find(id)?.Kind == ExtensionKind.BuiltIn ? _host.IsActive(id) : _bridge?.IsActive(id) ?? false;

	public TimeSpan? ActivationTime(string id) => Find(id)?.Kind == ExtensionKind.BuiltIn ? _host.ActivationTime(id) : _bridge?.ActivationTime(id);

	public long? HostMemory(string id) => Find(id)?.Kind == ExtensionKind.Installed && IsActive(id) ? _bridge?.HostMemory() : null;

	public bool NeedsRestart(string id) => _restart.Contains(id);

	public void SetEnabled(string id, bool enabled, EnablementScope scope) {
		bool was = _enablement.IsEnabled(id);
		_enablement.Set(id, enabled, scope);
		bool now = _enablement.IsEnabled(id);

		if (was != now && Find(id) is { } entry) {
			if (entry.Kind == ExtensionKind.BuiltIn) ApplyBuiltIn(id, now);
			else _ = ApplyInstalledAsync(id, now);
		}

		Raise();
	}

	private void ApplyBuiltIn(string id, bool enabled) {
		if (!enabled) {
			_host.Unload(id);
			return;
		}

		IForgeExtension? extension = _host.All().FirstOrDefault(item => string.Equals(item.GetIdentifier(), id, StringComparison.OrdinalIgnoreCase));

		if (extension is null) return;

		ActivationEvent activation = extension.GetActivation();

		if (activation.HasFlag(ActivationEvent.Startup) || (activation.HasFlag(ActivationEvent.WorkspaceContains) && extension.ActivatesIn(_commandHost.GetWorkspacePath()))) _host.Load(id, _commandHost);
	}

	private async Task ApplyInstalledAsync(string id, bool enabled) {
		if (_bridge is not { } bridge) return;

		if (!enabled) {
			_restart.Remove(id);
			await bridge.DisableAsync(id).ConfigureAwait(false);
		}
		else if (!await bridge.EnableAsync(id).ConfigureAwait(false)) _restart.Add(id);
		else _restart.Remove(id);

		Raise();
	}

	public bool IsCheckingAll { get; private set; }

	public ExtensionPermissions? PermissionStore { get; init; }

	public IReadOnlyList<PermissionRule> Permissions(string id) => PermissionStore is { } store && Find(id) is { Kind: ExtensionKind.Installed } entry ? store.Rules(id, entry.Version) : [];

	public void SetPermission(string id, string kind, string target, bool allowed) {
		if (PermissionStore is not { } store || Find(id) is not { Kind: ExtensionKind.Installed } entry) return;

		store.Set(id, entry.Version, kind, target, allowed);
		_bridge?.PushPermissions();

		Raise();
	}

	public void RevokePermission(string id, string kind, string target) {
		PermissionStore?.Remove(id, kind, target);
		_bridge?.PushPermissions();
		Raise();
	}

	public ExtensionEntry? AvailableUpdate(string id) {
		lock (_sync) return _updates.TryGetValue(id, out ExtensionEntry? update) ? update : null;
	}

	public ExtensionOperation Operation(string id) {
		lock (_sync) return _operations.TryGetValue(id, out ExtensionOperation operation) ? operation : ExtensionOperation.None;
	}

	public string? LastError(string id) {
		lock (_sync) return _errors.TryGetValue(id, out string? error) ? error : null;
	}

	private void SetOperation(string id, ExtensionOperation operation, string? error = null) {
		lock (_sync) {
			if (operation == ExtensionOperation.None) _operations.Remove(id);
			else _operations[id] = operation;

			if (error is null) _errors.Remove(id);
			else _errors[id] = error;
		}

		Raise();
	}

	public async Task<IReadOnlyList<ExtensionEntry>> SearchAsync(string query, CancellationToken cancellation) {
		List<GalleryExtension> found;

		try {
			found = await GalleryClient.SearchAsync(query, 50, cancellation).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) {
			return [];
		}

		HashSet<string> malicious = await MaliciousAsync(cancellation).ConfigureAwait(false);
		List<ExtensionEntry> entries = [];

		foreach (GalleryExtension extension in found) {
			if (GalleryClient.IsBlocked(malicious, extension)) continue;

			ExtensionEntry entry = ToEntry(extension);
			entries.Add(entry);

			lock (_sync) {
				_remote[entry.Id] = entry;
				_gallery[entry.Id] = extension;
			}
		}

		_ = FetchIconsAsync(found, cancellation);
		return entries;
	}

	private async Task<HashSet<string>> MaliciousAsync(CancellationToken cancellation) => _malicious ??= await GalleryClient.MaliciousAsync(cancellation).ConfigureAwait(false);

	private static ExtensionEntry ToEntry(GalleryExtension extension) => new(extension.Id, extension.DisplayName, extension.PublisherDisplayName, extension.Version, extension.Description, ExtensionKind.Marketplace) {
		Verified = extension.Verified,
		Installs = extension.Installs,
		Rating = extension.Rating,
		RatingCount = extension.RatingCount,
		LastUpdated = extension.LastUpdated,
		Engine = extension.Engine,
		Categories = extension.Categories,
		Repository = $"{GalleryClient.ItemUrl}?itemName={extension.Id}",
		IconPath = IconFile(extension) is { } icon && File.Exists(icon) ? icon : null,
		LoadReadme = cancellation => GalleryClient.TextAssetAsync(extension, GalleryClient.ReadmeAsset, cancellation),
		LoadChangelog = cancellation => GalleryClient.TextAssetAsync(extension, GalleryClient.ChangelogAsset, cancellation),
		LoadManifest = cancellation => GalleryClient.TextAssetAsync(extension, GalleryClient.ManifestAsset, cancellation),
	};

	private static string? IconFile(GalleryExtension extension) => extension.Asset(GalleryClient.IconAsset) is null ? null : Path.Combine(IconCache, $"{extension.Id}-{extension.Version}.png".ToLowerInvariant());

	private async Task FetchIconsAsync(List<GalleryExtension> extensions, CancellationToken cancellation) {
		bool any = false;

		foreach (GalleryExtension extension in extensions) {
			if (IconFile(extension) is not { } file || File.Exists(file) || extension.Asset(GalleryClient.IconAsset) is not { } url) continue;

			if (await GalleryClient.BytesAsync(url, cancellation).ConfigureAwait(false) is not { } bytes) continue;

			try {
				Directory.CreateDirectory(IconCache);
				await File.WriteAllBytesAsync(file, bytes, cancellation).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TaskCanceledException) {
				continue;
			}

			lock (_sync) {
				if (_remote.TryGetValue(extension.Id, out ExtensionEntry? entry)) _remote[extension.Id] = entry with { IconPath = file };
			}

			any = true;
		}

		if (any) Raise();
	}

	public async Task CheckForUpdatesAsync(string? id) {
		List<ExtensionEntry> targets = [.. _entries.Where(entry => entry.Kind == ExtensionKind.Installed && (id is null || string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)))];

		if (targets.Count == 0) return;

		if (id is null) IsCheckingAll = true;

		foreach (ExtensionEntry target in targets) SetOperation(target.Id, ExtensionOperation.Checking);

		try {
			Dictionary<string, GalleryExtension> latest = await GalleryClient.LatestAsync([.. targets.Select(static target => target.Id)], CancellationToken.None).ConfigureAwait(false);

			foreach (ExtensionEntry target in targets) {
				lock (_sync) {
					if (latest.TryGetValue(target.Id, out GalleryExtension? found) && IsNewer(found.Version, target.Version)) {
						_updates[target.Id] = ToEntry(found);
						_gallery[target.Id] = found;
					}
					else _updates.Remove(target.Id);
				}

				SetOperation(target.Id, ExtensionOperation.None, latest.ContainsKey(target.Id) ? null : "Not found in the marketplace");
			}
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) {
			foreach (ExtensionEntry target in targets) SetOperation(target.Id, ExtensionOperation.None, "Could not reach the marketplace");
		}
		finally {
			IsCheckingAll = false;
			Raise();
		}
	}

	private static bool IsNewer(string candidate, string current) => Version.TryParse(candidate.Split('-')[0], out Version? left) && Version.TryParse(current.Split('-')[0], out Version? right) ? left > right : !string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase);

	public async Task InstallAsync(ExtensionEntry entry) {
		GalleryExtension? extension;

		lock (_sync) {
			if (_operations.ContainsKey(entry.Id)) return;

			_gallery.TryGetValue(entry.Id, out extension);
		}

		SetOperation(entry.Id, ExtensionOperation.Installing);

		try {
			extension ??= await GalleryClient.LatestAsync(entry.Id, CancellationToken.None).ConfigureAwait(false) ?? throw new InvalidOperationException("The extension is no longer in the marketplace.");

			if (GalleryClient.IsBlocked(await MaliciousAsync(CancellationToken.None).ConfigureAwait(false), extension)) throw new InvalidOperationException("This extension is on the marketplace's malicious list and was blocked.");

			if (_bridge is { } running && running.IsActive(extension.Id)) await running.DisableAsync(extension.Id).ConfigureAwait(false);

			await GalleryClient.InstallAsync(extension, VSCodeExtensions.DefaultDirectory, CancellationToken.None).ConfigureAwait(false);

			PermissionStore?.Clear(extension.Id);

			lock (_sync) _updates.Remove(extension.Id);

			_bridge?.Rediscover();

			if (_enablement.IsEnabled(extension.Id) && !(_bridge is { } bridge && await bridge.EnableAsync(extension.Id).ConfigureAwait(false))) _restart.Add(extension.Id);
			else _restart.Remove(extension.Id);

			SetOperation(extension.Id, ExtensionOperation.None);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) {
			SetOperation(entry.Id, ExtensionOperation.None, exception.Message);
		}

		Rebuild();
	}

	public async Task UninstallAsync(string id) {
		if (Find(id) is not { Kind: ExtensionKind.Installed, Directory: { } directory }) return;

		SetOperation(id, ExtensionOperation.Uninstalling);

		try {
			if (_bridge is { } bridge && bridge.IsActive(id)) await bridge.DisableAsync(id).ConfigureAwait(false);

			GalleryClient.Uninstall(directory);

			PermissionStore?.Clear(id);

			string storage = Path.Combine(UserData.Directory, "vscode-storage", id);

			if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);

			lock (_sync) _updates.Remove(id);

			_restart.Remove(id);
			SetOperation(id, ExtensionOperation.None);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			_restart.Add(id);
			SetOperation(id, ExtensionOperation.None, $"Some files are in use; restart Forge to finish ({exception.Message})");
		}

		_bridge?.Rediscover();
		Rebuild();
	}
}
