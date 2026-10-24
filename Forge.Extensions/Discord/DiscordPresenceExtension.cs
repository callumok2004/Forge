using System.Text.Json;
using System.Text.RegularExpressions;

using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Discord;

public sealed class DiscordPresenceExtension : ForgeExtension
{
	private IOutputChannel? _output;

	private void Log(ICommandHost host, string line) =>
		(_output ??= host.Channel("Discord Presence")).Append(line);

	private const string IdleImageKey = "idle-vscode";
	private const string EditorImageKey = "vscode";

	private DiscordIpcClient? _client;
	private ICommandHost? _host;
	private long? _startTimestamp;
	private DateTime _lastSent = DateTime.MinValue;

	public override string Identifier => "forge.discord";

	public override string DisplayName => "Discord Presence";

	public override ActivationEvent Activation => ActivationEvent.Startup;

	public bool Enabled { get; set; } = true;

	public string AppDisplayName { get; set; } = "Forge";

	public string DetailsIdling { get; set; } = "Idling";

	public string DetailsEditing { get; set; } = "Editing {file_name}";

	public string LowerDetailsIdling { get; set; } = "Idling";

	public string DetailsDebugging { get; set; } = "Debugging {file_name}";

	public string LowerDetailsEditing { get; set; } = "Workspace: {workspace}";

	public string LowerDetailsDebugging { get; set; } = "Debugging: {workspace}";

	public bool SuppressNotifications { get; set; }

	public int IdleTimeout { get; set; }

	public string LowerDetailsNoWorkspaceFound { get; set; } = "No workspace";

	public string LargeImageIdling { get; set; } = "Idling";

	public string LargeImage { get; set; } = "Editing a {LANG} file";

	public string SmallImage { get; set; } = "{app_name}";

	public bool RemoveDetails { get; set; }

	public bool RemoveLowerDetails { get; set; }

	public bool RemoveTimestamp { get; set; }

	public bool RemoveRemoteRepository { get; set; }

	public bool SwapBigAndSmallImage { get; set; }

	public string[] WorkspaceExcludePatterns { get; set; } = [];

	protected override void Configure(Settings settings) {
		Enabled = settings.GetBoolean("discord.enabled") ?? Enabled;
		AppDisplayName = settings.GetString("discord.appDisplayName") ?? AppDisplayName;

		DetailsIdling = settings.GetString("discord.detailsIdling") ?? DetailsIdling;
		DetailsEditing = settings.GetString("discord.detailsEditing") ?? DetailsEditing;
		DetailsDebugging = settings.GetString("discord.detailsDebugging") ?? DetailsDebugging;

		LowerDetailsIdling = settings.GetString("discord.lowerDetailsIdling") ?? LowerDetailsIdling;
		LowerDetailsEditing = settings.GetString("discord.lowerDetailsEditing") ?? LowerDetailsEditing;
		LowerDetailsDebugging = settings.GetString("discord.lowerDetailsDebugging") ?? LowerDetailsDebugging;
		LowerDetailsNoWorkspaceFound =
			settings.GetString("discord.lowerDetailsNoWorkspaceFound") ?? LowerDetailsNoWorkspaceFound;

		LargeImageIdling = settings.GetString("discord.largeImageIdling") ?? LargeImageIdling;
		LargeImage = settings.GetString("discord.largeImage") ?? LargeImage;
		SmallImage = settings.GetString("discord.smallImage") ?? SmallImage;

		RemoveDetails = settings.GetBoolean("discord.removeDetails") ?? RemoveDetails;
		RemoveLowerDetails = settings.GetBoolean("discord.removeLowerDetails") ?? RemoveLowerDetails;
		RemoveTimestamp = settings.GetBoolean("discord.removeTimestamp") ?? RemoveTimestamp;
		RemoveRemoteRepository =
			settings.GetBoolean("discord.removeRemoteRepository") ?? RemoveRemoteRepository;

		SwapBigAndSmallImage =
			settings.GetBoolean("discord.swapBigAndSmallImage") ?? SwapBigAndSmallImage;

		SuppressNotifications =
			settings.GetBoolean("discord.suppressNotifications") ?? SuppressNotifications;

		IdleTimeout = settings.GetInt32("discord.idleTimeout") ?? IdleTimeout;

		WorkspaceExcludePatterns =
			settings.GetStringArray("discord.workspaceExcludePatterns") ?? WorkspaceExcludePatterns;
	}

	public override void Register(ICommandRegistry commands) {
		commands.Register(new Command("discord.enable", "Enable", "Discord Presence", host => {
			Enabled = true;
			Disconnect(host);
			Connect(host);
		}));

		commands.Register(new Command("discord.disable", "Disable", "Discord Presence", host => {
			Enabled = false;
			Disconnect(host);
			host.RemoveStatusItem("discord.presence");
			Log(host, "disabled.");
			host.Refresh();
		}));

		commands.Register(new Command("discord.reconnect", "Reconnect", "Discord Presence", host => {
			Disconnect(host);
			Connect(host);
		}));

		commands.Register(new Command("discord.disconnect", "Disconnect", "Discord Presence", host => {
			Disconnect(host);

			host.SetStatusItem("discord.presence", "Pulse", "Reconnect to Discord", "discord.reconnect");
			Log(host, "disconnected.");
			host.Refresh();
		}));
	}

	protected override void OnActivate(IExtensionContext context) {
		ICommandHost host = context.GetHost();

		_host = host;

		if (!Enabled || IsWorkspaceExcluded(host.GetWorkspacePath())) return;

		Connect(host);
	}

	private bool IsWorkspaceExcluded(string? workspacePath) {
		if (workspacePath is null) return false;

		foreach (string pattern in WorkspaceExcludePatterns) {
			try {
				if (Regex.IsMatch(workspacePath, pattern)) return true;
			}
			catch (ArgumentException) {
			}
		}

		return false;
	}

	private void Connect(ICommandHost host) {
		host.SetStatusItem("discord.presence", "Pulse", "Connecting to Discord...", "discord.reconnect");
		host.Refresh();

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			DiscordIpcClient client = new();

			client.Ready += () => {
				host.SetStatusItem("discord.presence", "Globe", "Connected to Discord", "discord.disconnect");
				Log(host, "connected.");

				_startTimestamp ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

				Update(force: true);
				host.Refresh();
			};

			client.Disconnected += () => {
				host.SetStatusItem("discord.presence", "Pulse", "Reconnect to Discord", "discord.reconnect");
				host.Refresh();
			};

			_client = client;

			if (!client.Login()) {
				host.SetStatusItem("discord.presence", "Pulse", "Reconnect to Discord", "discord.reconnect");
				Log(host, "no Discord client detected.");
				host.Refresh();
			}
		}, null);
	}

	private void Disconnect(ICommandHost host) {
		_client?.ClearActivity();
		_client?.Dispose();
		_client = null;
		_startTimestamp = null;
	}

	public void Update(bool force = false) {
		if (_client is not { IsConnected: true } client || _host is not { } host) return;

		if (!force && DateTime.UtcNow - _lastSent < TimeSpan.FromSeconds(2)) return;

		_lastSent = DateTime.UtcNow;

		string? file = host.GetActiveFilePath();
		string language = file is null ? "text" : DiscordLanguages.IconFor(file);

		string largeText = Fill(LargeImage, host, language);
		string smallText = Fill(SmallImage, host, language);

		DiscordActivityPayload activity = new() {
			Name = AppDisplayName.Length > 1 ? AppDisplayName : null,
			Details = RemoveDetails
				? null
				: Fill(file is null ? DetailsIdling : DetailsEditing, host, language),
			State = RemoveLowerDetails
				? null
				: Fill(file is null ? LowerDetailsIdling : LowerDetailsEditing, host, language),
			StartTimestamp = RemoveTimestamp ? null : _startTimestamp,
		};

		string largeKey = file is null ? IdleImageKey : language;
		string largeKeyText = file is null ? LargeImageIdling : largeText;

		if (SwapBigAndSmallImage) {
			activity.LargeImageKey = EditorImageKey;
			activity.LargeImageText = smallText;
			activity.SmallImageKey = largeKey;
			activity.SmallImageText = largeKeyText;
		}
		else {
			activity.LargeImageKey = largeKey;
			activity.LargeImageText = largeKeyText;
			activity.SmallImageKey = EditorImageKey;
			activity.SmallImageText = smallText;
		}

		if (!RemoveRemoteRepository && host.GetRemoteUrl() is { Length: > 0 } remote) {
			activity.Button = ("View Repository", remote);
		}

		client.SetActivity(activity);
	}

	private string Fill(string template, ICommandHost host, string language) {
		if (template.Length == 0) return DiscordIpcClient.FakeEmpty;

		string? file = host.GetActiveFilePath();
		string fileName = file is null ? DiscordIpcClient.FakeEmpty : Path.GetFileName(file);

		string directory = file is null
			? DiscordIpcClient.FakeEmpty
			: Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);

		string workspace = host.GetWorkspacePath() is { } root
			? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))
			: LowerDetailsNoWorkspaceFound;

		return template
			.Replace("{empty}", DiscordIpcClient.FakeEmpty, StringComparison.Ordinal)
			.Replace("{app_name}", AppDisplayName, StringComparison.Ordinal)
			.Replace("{file_name}", fileName, StringComparison.Ordinal)
			.Replace("{dir_name}", directory, StringComparison.Ordinal)
			.Replace("{full_dir_name}", Path.GetDirectoryName(file) ?? DiscordIpcClient.FakeEmpty,
				StringComparison.Ordinal)
			.Replace("{workspace}", workspace, StringComparison.Ordinal)
			.Replace("{workspace_folder}", workspace, StringComparison.Ordinal)
			.Replace("{workspace_and_folder}", workspace, StringComparison.Ordinal)
			.Replace("{current_line}", host.GetCaretLine().ToString("N0"), StringComparison.Ordinal)
			.Replace("{current_column}", host.GetCaretColumn().ToString("N0"), StringComparison.Ordinal)
			.Replace("{total_lines}", host.GetLineCount().ToString("N0"), StringComparison.Ordinal)
			.Replace("{current_errors}", host.GetErrorCount().ToString("N0"), StringComparison.Ordinal)
			.Replace("{git_branch}", host.GetBranchName() ?? "Unknown", StringComparison.Ordinal)
			.Replace("{git_repo_name}", RepositoryName(host.GetRemoteUrl()), StringComparison.Ordinal)
			.Replace("{lang}", language.ToLowerInvariant(), StringComparison.Ordinal)
			.Replace("{Lang}", Title(language), StringComparison.Ordinal)
			.Replace("{LANG}", language.ToUpperInvariant(), StringComparison.Ordinal);
	}

	private static string Title(string value) =>
		value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

	private static string RepositoryName(string? remote) {
		if (remote is null or { Length: 0 }) return "Unknown";

		string name = remote.TrimEnd('/');
		int slash = name.LastIndexOf('/');

		if (slash >= 0) name = name[(slash + 1)..];

		return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
	}
}

public static class DiscordLanguages
{
	private sealed record Rule(Regex? Pattern, string? Suffix, string Image);

	private static readonly Lazy<(Rule[] Rules, Dictionary<string, string> Languages)> Table = new(Load);

	private static string? _lastPath;
	private static string _lastIcon = "text";

	public static string IconFor(string path) {
		if (string.Equals(path, Volatile.Read(ref _lastPath), StringComparison.Ordinal)) return _lastIcon;

		string icon = Resolve(Path.GetFileName(path));

		_lastIcon = icon;
		Volatile.Write(ref _lastPath, path);
		return icon;
	}

	private static string Resolve(string fileName) {
		(Rule[] rules, Dictionary<string, string> languages) = Table.Value;

		foreach (Rule rule in rules)
			if (rule.Pattern is { } pattern ? pattern.IsMatch(fileName) : fileName.EndsWith(rule.Suffix!, StringComparison.OrdinalIgnoreCase)) return rule.Image;

		return languages.TryGetValue(Path.GetExtension(fileName).TrimStart('.'), out string? byLanguage) ? byLanguage : "text";
	}

	private static (Rule[], Dictionary<string, string>) Load() {
		List<Rule> rules = [];
		Dictionary<string, string> languages = [with(StringComparer.OrdinalIgnoreCase)];

		try {
			string path = Path.Combine(AppContext.BaseDirectory, "Extensions", "Discord", "discord-languages.json");

			if (!File.Exists(path)) return ([], languages);

			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

			if (document.RootElement.TryGetProperty("KNOWN_EXTENSIONS", out JsonElement known)) {
				foreach (JsonProperty property in known.EnumerateObject()) {
					if (!property.Value.TryGetProperty("image", out JsonElement image) || image.GetString() is not { } value) continue;

					if (ParsePattern(property.Name) is { } pattern) rules.Add(new Rule(pattern, null, value));
					else if (!property.Name.StartsWith('/')) rules.Add(new Rule(null, property.Name, value));
				}
			}

			if (document.RootElement.TryGetProperty("KNOWN_LANGUAGES", out JsonElement list)) {
				foreach (JsonElement entry in list.EnumerateArray())
					if (entry.TryGetProperty("language", out JsonElement language) && entry.TryGetProperty("image", out JsonElement image) && language.GetString() is { } id && image.GetString() is { } value) languages[id] = value;
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException) {
		}

		return ([.. rules], languages);
	}

	private static Regex? ParsePattern(string key) {
		if (!key.StartsWith('/')) return null;

		int end = key.LastIndexOf('/');

		if (end <= 0) return null;

		try {
			return new Regex(key[1..end], key[(end + 1)..].Contains('i') ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
		}
		catch (ArgumentException) {
			return null;
		}
	}
}
