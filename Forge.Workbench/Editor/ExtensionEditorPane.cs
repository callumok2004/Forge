using System.Globalization;
using System.Text;
using System.Text.Json;

using Forge.Common.Extensions;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core;
using Forge.Language;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ExtensionEditorPane : EditorPane
{
	public const string PaneId = "workbench.editor.extension";

	private const float MaxWidth = 960f;
	private const float SidePadding = 28f;
	private const float HeaderTop = 24f;
	private const float IconSize = 84f;
	private const float HeaderHeight = 150f;
	private const float IconGap = 20f;
	private const float ButtonHeight = 26f;
	private const float ButtonPadding = 12f;
	private const float ChipHeight = 20f;
	private const float TabHeight = 34f;
	private const float BodyGap = 18f;
	private const float ScrollbarWidth = 10f;

	private enum Tab : byte
	{
		Details,
		Features,
		Changelog,
		Review,
		Permissions,
	}

	private readonly record struct HitBox(float X, float Y, float Width, float Height, Action Action, string Key);

	private string _id;
	private readonly IExtensionCatalog _catalog;
	private readonly TextSelection _selection = new();
	private readonly List<HitBox> _hits = [];

	private HoverMarkdownLayout? _markdown;
	private Tab _tab;
	private float _layoutWidth = -1f;
	private Tab _layoutTab;
	private float _bodyTop;
	private float _contentBottom;
	private float _scrollY;
	private string? _hoverKey;
	private string? _hoverHref;
	private IImage? _icon;
	private bool _iconLoaded;
	private bool _reviewing;
	private readonly Dictionary<(string, Tab), string> _remoteText = [];
	private readonly HashSet<(string, Tab)> _loading = [];

	public ExtensionEditorPane(string id, IExtensionCatalog catalog) {
		_id = id;
		_catalog = catalog;
		Id = PaneId;
		Name = "ExtensionEditor";
		Dock = DockType.Fill;
		KeyboardInputEnabled = true;
		catalog.Changed += CatalogChanged;
	}

	public static Func<string, IImage?>? ImageLoader { get; set; }

	public Action<string>? OpenLink { get; set; }

	public override string Title => $"Extension: {Entry?.DisplayName ?? _id}";

	public override Codicon Icon => Codicon.Extensions;

	public override void OnClosed() => _catalog.Changed -= CatalogChanged;

	private ExtensionEntry? Entry => _catalog.Find(_id);

	public void Show(string id) {
		if (string.Equals(_id, id, StringComparison.OrdinalIgnoreCase)) return;

		_id = id;
		_tab = Tab.Details;
		_scrollY = 0f;
		_layoutWidth = -1f;
		_icon = null;
		_iconLoaded = false;
		_reviewing = false;
		Repaint();
	}

	public Action<Action>? Post { get; set; }

	private void CatalogChanged() {
		if (_icon is null) _iconLoaded = false;

		_layoutWidth = -1f;
		Repaint();
	}

	private float FrameLeft => MathF.Max(0f, (Wide - (MaxWidth * Scale)) / 2f) + (SidePadding * Scale);

	private float FrameWidth => MathF.Max(1f, MathF.Min(Wide, MaxWidth * Scale) - (SidePadding * 2f * Scale));

	private float BodyViewTop => _bodyTop;

	private float MaxScroll => MathF.Max(0f, _contentBottom + PermissionRowsHeight - (Tall - BodyViewTop) + (BodyGap * Scale));

	private IReadOnlyList<Tab> Tabs {
		get {
			if (_reviewing) return [Tab.Review];

			List<Tab> tabs = [Tab.Details];

			if (Entry?.Kind != ExtensionKind.Marketplace) tabs.Add(Tab.Features);

			if (Entry?.ChangelogPath is not null || Entry?.LoadChangelog is not null) tabs.Add(Tab.Changelog);

			if (Entry?.Kind == ExtensionKind.Installed) tabs.Add(Tab.Permissions);

			return tabs;
		}
	}

	private void EnsureLayout() {
		if (Ui is null || Entry is not { } entry) return;

		float scale = Scale;
		_bodyTop = (HeaderTop + HeaderHeight + TabHeight + BodyGap) * scale;

		if (_layoutWidth == Wide && _layoutTab == _tab && _markdown is not null) return;

		_layoutWidth = Wide;
		_layoutTab = _tab;
		_selection.Clear();
		_markdown = new HoverMarkdownLayout(Ui.GetFonts(), scale, _selection);
		_contentBottom = _markdown.Layout(HoverMarkdown.Parse(Markdown(entry, _tab)), 0f, FrameWidth, 0f, 0f, []);
		_scrollY = Math.Clamp(_scrollY, 0f, MaxScroll);
	}

	private string Markdown(ExtensionEntry entry, Tab tab) => tab switch {
		Tab.Review => Review(entry, Remote(entry, Tab.Review, entry.LoadManifest)),
		Tab.Changelog => Read(entry.ChangelogPath) ?? Remote(entry, tab, entry.LoadChangelog) ?? (entry.LoadChangelog is not null ? "Loading…" : "No changelog."),
		Tab.Features => Features(entry),
		Tab.Permissions => PermissionsIntro,
		_ => Read(entry.ReadmePath) ?? Remote(entry, tab, entry.LoadReadme) ?? (entry.LoadReadme is not null ? "Loading…" : Synthesised(entry)),
	};

	private string? Remote(ExtensionEntry entry, Tab tab, Func<CancellationToken, Task<string?>>? load) {
		if (load is null) return null;

		(string, Tab) key = (entry.Id + "@" + entry.Version, tab);

		if (_remoteText.TryGetValue(key, out string? text)) return text;

		if (_loading.Add(key)) _ = LoadAsync(key, load);

		return null;
	}

	private async Task LoadAsync((string, Tab) key, Func<CancellationToken, Task<string?>> load) {
		string? text = null;

		try {
			text = await load(CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException) {
		}

		void Apply() {
			_remoteText[key] = text ?? "Could not load this from the marketplace.";
			_loading.Remove(key);
			_layoutWidth = -1f;
			Repaint();
		}

		if (Post is { } post) post(Apply);
	}

	private static string Review(ExtensionEntry entry, string? manifest) {
		StringBuilder builder = new();
		builder.Append("## Review ").Append(Escape(entry.DisplayName)).Append(' ').AppendLine(entry.Version).AppendLine();
		builder.AppendLine("| | |").AppendLine("|:--|:--|");
		builder.Append("| Publisher | ").Append(Escape(entry.Publisher)).AppendLine(entry.Verified ? " $(verified-filled) verified |" : " **not verified** |");
		builder.Append("| Installs | ").Append(entry.Installs.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" |");

		if (entry.RatingCount > 0) builder.Append("| Rating | ").Append(entry.Rating.ToString("0.0", CultureInfo.InvariantCulture)).Append(" / 5 (").Append(entry.RatingCount).AppendLine(" ratings) |");

		if (entry.LastUpdated is { } updated) builder.Append("| Last updated | ").Append(updated.ToString("d MMM yyyy", CultureInfo.InvariantCulture)).AppendLine(" |");

		if (entry.Engine is { Length: > 0 } engine) builder.Append("| VS Code engine | `").Append(engine).AppendLine("` |");

		if (entry.Categories.Count > 0) builder.Append("| Categories | ").Append(Escape(string.Join(", ", entry.Categories))).AppendLine(" |");

		builder.AppendLine();

		if (manifest is null) {
			builder.AppendLine("Loading the manifest…");
			return builder.ToString();
		}

		try {
			using JsonDocument document = JsonDocument.Parse(manifest, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			JsonElement root = document.RootElement;

			builder.AppendLine("### What it runs").AppendLine();

			if (root.TryGetProperty("main", out JsonElement main) && main.ValueKind == JsonValueKind.String) builder.Append("* Code entry point `").Append(main.GetString()).AppendLine("`");
			else builder.AppendLine("* No code entry point; it only contributes declarations");

			if (root.TryGetProperty("activationEvents", out JsonElement events) && events.ValueKind == JsonValueKind.Array) {
				List<string> list = [.. events.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).Where(static item => item.Length > 0)];

				if (list.Contains("*")) builder.AppendLine("* **Activates on startup** (`*`)");
				else if (list.Count > 0) builder.Append("* Activates on ").AppendLine(string.Join(", ", list.Take(8).Select(static item => "`" + item + "`")) + (list.Count > 8 ? $" and {list.Count - 8} more" : string.Empty));
			}

			AppendDependencies(builder, root, "extensionDependencies", "* Depends on ");
			AppendDependencies(builder, root, "extensionPack", "* Bundles ");

			if (root.TryGetProperty("contributes", out JsonElement contributes) && contributes.ValueKind == JsonValueKind.Object) {
				builder.AppendLine().AppendLine("### Contributes").AppendLine();

				foreach (JsonProperty property in contributes.EnumerateObject()) {
					int count = property.Value.ValueKind switch {
						JsonValueKind.Array => property.Value.GetArrayLength(),
						JsonValueKind.Object => property.Value.EnumerateObject().Count(),
						_ => 1,
					};

					builder.Append("* `").Append(property.Name).Append("` (").Append(count).AppendLine(")");
				}
			}
		}
		catch (JsonException) {
			builder.AppendLine("The manifest could not be read.");
		}

		builder.AppendLine().AppendLine("Installing runs this extension's code in Forge's extension host with your user account's access.");
		return builder.ToString();
	}

	private static void AppendDependencies(StringBuilder builder, in JsonElement root, string name, string prefix) {
		if (!root.TryGetProperty(name, out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Array || dependencies.GetArrayLength() == 0) return;

		builder.Append(prefix).AppendLine(string.Join(", ", dependencies.EnumerateArray().Select(static item => "`" + item.GetString() + "`")));
	}

	private static string? Read(string? path) {
		if (path is null) return null;

		try {
			return File.ReadAllText(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	private const string PermissionsIntro = "Without asking, this extension may read and write its own folder, its storage and the temp folder, read the open workspace, write the workspace through the editor API, and connect to localhost.\n\nAnything else (running programs, network hosts, native modules, files elsewhere) is blocked and raises a notification so you can allow or deny it. Every rule resets when the extension updates.";

	private const float PermissionRowHeight = 34f;

	private float PermissionRowsHeight => Entry is { } entry && _tab == Tab.Permissions ? (MathF.Max(1, _catalog.Permissions(entry.Id).Count) * PermissionRowHeight * Scale) + (12f * Scale) : 0f;

	private void PaintPermissions(IPainter painter, ExtensionEntry entry, float left, float originY) {
		float scale = Scale;
		float rowHeight = PermissionRowHeight * scale;
		float y = originY + _contentBottom + (12f * scale);
		ITextFont font = painter.GetUi();
		ITextFont strong = painter.GetUiStrong();
		IReadOnlyList<Forge.Core.PermissionRule> rules = _catalog.Permissions(entry.Id);

		if (rules.Count == 0) {
			painter.DrawText(font, "No permissions have been requested yet.", ScreenX + left, ScreenY + y + ((rowHeight + font.GetAscent() - font.GetDescent()) / 2f), Theme.DescriptionForeground);
			return;
		}

		foreach (Forge.Core.PermissionRule rule in rules) {
			float middle = ScreenY + y + (rowHeight / 2f);
			float baseline = middle + ((font.GetAscent() - font.GetDescent()) / 2f);
			Codicon icon = rule.Kind switch { "process" => Codicon.Terminal, "network" => Codicon.Globe, "native" => Codicon.Package, "read" => Codicon.Eye, _ => Codicon.Edit };

			painter.FillRectangle(ScreenX + left, ScreenY + y + rowHeight - scale, FrameWidth, MathF.Max(1f, scale), Theme.PanelBorder.Transparency(0.6f));
			painter.DrawIcon(icon, ScreenX + left + (9f * scale), middle, 16f * scale, Theme.IconForeground);

			string kind = rule.Kind switch { "process" => "Run", "network" => "Connect", "native" => "Native", "read" => "Read", _ => "Write" };
			painter.DrawText(strong, kind, ScreenX + left + (26f * scale), baseline, Theme.Foreground);
			painter.DrawText(font, rule.Target, ScreenX + left + (96f * scale), baseline, Theme.Foreground.Transparency(0.9f));

			string id = entry.Id;
			Forge.Core.PermissionRule current = rule;
			float buttonTop = y + ((rowHeight - (ButtonHeight * scale)) / 2f);
			float right = left + FrameWidth;
			float removeWidth = font.Measure("Remove") + (ButtonPadding * 2f * scale);
			string flipLabel = rule.Allowed ? "Deny" : "Allow";
			float flipWidth = font.Measure(flipLabel) + (ButtonPadding * 2f * scale);
			float stateWidth = small(painter).Measure(rule.Allowed ? "Allowed" : "Denied");

			Button(painter, "Remove", right - removeWidth, buttonTop, primary: false, $"perm.remove.{rule.Kind}.{rule.Target}", () => _catalog.RevokePermission(id, current.Kind, current.Target));
			Button(painter, flipLabel, right - removeWidth - flipWidth - (8f * scale), buttonTop, primary: false, $"perm.flip.{rule.Kind}.{rule.Target}", () => _catalog.SetPermission(id, current.Kind, current.Target, !current.Allowed));
			painter.DrawText(small(painter), rule.Allowed ? "Allowed" : "Denied", ScreenX + right - removeWidth - flipWidth - stateWidth - (20f * scale), baseline, rule.Allowed ? Theme.SuccessForeground : Theme.ErrorForeground);

			y += rowHeight;
		}

		static ITextFont small(IPainter painter) => painter.GetUiSmall();
	}

	private static string Synthesised(ExtensionEntry entry) {
		StringBuilder builder = new();
		builder.Append("# ").AppendLine(Escape(entry.DisplayName)).AppendLine();

		if (entry.Description.Length > 0) builder.AppendLine(Escape(entry.Description)).AppendLine();

		builder.AppendLine(entry.Kind == ExtensionKind.BuiltIn ? "This extension ships with Forge and runs natively in-process." : "This extension runs in the sandboxed extension host.");
		return builder.ToString();
	}

	private static string Features(ExtensionEntry entry) {
		if (entry.Contributions.Count == 0) return "This extension does not contribute any commands or views.";

		StringBuilder builder = new();

		foreach (IGrouping<string, ExtensionContribution> group in entry.Contributions.GroupBy(static item => item.Kind)) {
			builder.Append("## ").Append(group.Key).Append(" (").Append(group.Count()).AppendLine(")").AppendLine();
			builder.AppendLine("| Name | ID |").AppendLine("|:--|:--|");

			foreach (ExtensionContribution item in group) builder.Append("| ").Append(Escape(item.Name).Replace("|", "\\|")).Append(" | `").Append(item.Detail ?? string.Empty).AppendLine("` |");

			builder.AppendLine();
		}

		return builder.ToString();
	}

	private static string Escape(string text) {
		StringBuilder builder = new(text.Length);

		foreach (char character in text) {
			if (character is '\\' or '*' or '_' or '`' or '[' or ']' or '#' or '<' or '>') builder.Append('\\');

			builder.Append(character);
		}

		return builder.ToString();
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		EnsureLayout();
		float previous = _scrollY;
		_scrollY = Math.Clamp(_scrollY - (delta / 120f * 60f * Scale), 0f, MaxScroll);

		if (_scrollY != previous) Repaint();

		return true;
	}

	public override void OnCursorMoved(float x, float y) {
		string? key = null;

		foreach (HitBox hit in _hits) {
			if (x >= hit.X && x < hit.X + hit.Width && y >= hit.Y && y < hit.Y + hit.Height) key = hit.Key;
		}

		string? href = LinkAt(x, y);

		if (key == _hoverKey && href == _hoverHref) return;

		_hoverKey = key;
		_hoverHref = href;
		Repaint();
	}

	public override void OnCursorExited() {
		_hoverKey = null;
		_hoverHref = null;
		Repaint();
	}

	private string? LinkAt(float x, float y) {
		if (_markdown is null || y < BodyViewTop) return null;

		float originX = FrameLeft;
		float originY = BodyViewTop - _scrollY;

		foreach (HoverMarkdownLayout.LinkBox link in _markdown.Links) {
			if (x >= originX + link.X && x < originX + link.X + link.Width && y >= originY + link.Y && y < originY + link.Y + link.Height) return link.Href;
		}

		return null;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) => true;

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return true;

		for (int i = _hits.Count - 1; i >= 0; i--) {
			HitBox hit = _hits[i];

			if (x >= hit.X && x < hit.X + hit.Width && y >= hit.Y && y < hit.Y + hit.Height) {
				hit.Action();
				return true;
			}
		}

		if (LinkAt(x, y) is { } href) OpenLink?.Invoke(href);

		return true;
	}

	public override void Paint(IPainter painter) {
		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.EditorBackground);

		if (Entry is not { } entry) {
			painter.DrawText(painter.GetUi(), "This extension is no longer installed.", ScreenX + (SidePadding * Scale), ScreenY + (40f * Scale), Theme.DescriptionForeground);
			return;
		}

		EnsureLayout();
		_hits.Clear();

		float scale = Scale;
		float left = FrameLeft;
		float top = HeaderTop * scale;

		PaintIcon(painter, entry, left, top);

		float textLeft = left + ((IconSize + IconGap) * scale);
		ITextFont title = painter.GetUi(22f, true);
		ITextFont body = painter.GetUi();
		ITextFont small = painter.GetUiSmall();
		bool enabled = _catalog.IsEnabled(entry.Id);

		float baseline = top + title.GetAscent();
		painter.DrawText(title, entry.DisplayName, ScreenX + textLeft, ScreenY + baseline, Theme.Foreground);

		float x = textLeft + title.Measure(entry.DisplayName) + (10f * scale);

		if (entry.Version.Length > 0) painter.DrawText(body, "v" + entry.Version, ScreenX + x, ScreenY + baseline, Theme.DescriptionForeground);

		baseline += 22f * scale;
		x = textLeft;

		if (entry.Kind == ExtensionKind.BuiltIn) {
			painter.DrawIcon(Codicon.VerifiedFilled, ScreenX + x + (7f * scale), ScreenY + baseline - (body.GetAscent() / 2.6f), 14f * scale, Theme.TextLinkForeground);
			x += 18f * scale;
		}

		x = DrawSegment(painter, body, entry.Publisher, x, baseline, Theme.Foreground);
		x = DrawSegment(painter, body, "|", x + (8f * scale), baseline, Theme.DescriptionForeground);
		DrawSegment(painter, body, entry.Id, x + (8f * scale), baseline, Theme.DescriptionForeground);

		baseline += 21f * scale;

		if (entry.Description.Length > 0) painter.DrawText(body, entry.Description, ScreenX + textLeft, ScreenY + baseline, Theme.Foreground.Transparency(0.85f));

		float rowTop = baseline + (12f * scale);
		PaintActions(painter, entry, enabled, textLeft, rowTop);
		PaintChips(painter, entry, enabled, textLeft, rowTop + ((ButtonHeight + 10f) * scale));

		float tabsTop = (HeaderTop + HeaderHeight) * scale;
		PaintTabs(painter, left, tabsTop);

		PaintBody(painter, left);
		PaintScrollbar(painter);
	}

	private void PaintIcon(IPainter painter, ExtensionEntry entry, float left, float top) {
		float scale = Scale;
		float size = IconSize * scale;

		if (!_iconLoaded) {
			_iconLoaded = true;
			_icon = entry.IconPath is { } path ? ImageLoader?.Invoke(path) : null;
		}

		if (_icon is { } image) {
			painter.DrawImage(image, ScreenX + left, ScreenY + top, size, size, false);
			return;
		}

		bool builtIn = entry.Kind == ExtensionKind.BuiltIn;
		painter.FillRoundedRectangle(ScreenX + left, ScreenY + top, size, size, Metrics.RadiusLarge * scale, (builtIn ? Theme.Accent : Theme.ButtonSecondaryBackground).Transparency(0.2f));
		painter.DrawIcon(builtIn ? Codicon.Gear : Codicon.Extensions, ScreenX + left + (size / 2f), ScreenY + top + (size / 2f), 40f * scale, builtIn ? Theme.Accent : Theme.Foreground);
	}

	private float DrawSegment(IPainter painter, ITextFont font, string text, float x, float baseline, Color color) {
		painter.DrawText(font, text, ScreenX + x, ScreenY + baseline, color);
		return x + font.Measure(text);
	}

	private float PaintActions(IPainter painter, ExtensionEntry entry, bool enabled, float x, float top) {
		string id = entry.Id;

		ExtensionOperation operation = _catalog.Operation(id);

		if (operation != ExtensionOperation.None) return Button(painter, operation switch { ExtensionOperation.Installing => "Installing…", ExtensionOperation.Uninstalling => "Uninstalling…", _ => "Checking…" }, x, top, primary: false, "busy", static () => { });

		if (_reviewing) {
			x = Button(painter, "Install", x, top, primary: true, "confirm", () => {
				_reviewing = false;
				_tab = Tab.Details;
				_layoutWidth = -1f;
				_ = _catalog.InstallAsync(entry);
			});
			return Button(painter, "Cancel", x + (8f * Scale), top, primary: false, "cancel", () => {
				_reviewing = false;
				_tab = Tab.Details;
				_layoutWidth = -1f;
				Repaint();
			});
		}

		if (entry.Kind == ExtensionKind.Marketplace) return Button(painter, "Install…", x, top, primary: true, "install", StartReview);

		if (entry.Kind == ExtensionKind.Installed && _catalog.AvailableUpdate(id) is { } update) x = Button(painter, $"Update to {update.Version}", x, top, primary: true, "update", () => _ = _catalog.InstallAsync(update)) + (8f * Scale);

		x = Button(painter, enabled ? "Disable" : "Enable", x, top, primary: !enabled, "toggle", () => _catalog.SetEnabled(id, !enabled, EnablementScope.Global));

		if (entry.Kind == ExtensionKind.Installed) {
			x = Button(painter, "Uninstall", x + (8f * Scale), top, primary: false, "uninstall", () => _ = _catalog.UninstallAsync(id));
			x = Button(painter, "Check for Update", x + (8f * Scale), top, primary: false, "check", () => _ = _catalog.CheckForUpdatesAsync(id));
		}

		if (_catalog.HasWorkspace) {
			bool? workspace = _catalog.WorkspaceOverride(id);
			string label = workspace is null ? enabled ? "Disable for Workspace" : "Enable for Workspace" : "Use Global Setting";
			x = Button(painter, label, x + (8f * Scale), top, primary: false, "workspace", () => _catalog.SetEnabled(id, workspace is null ? !enabled : _catalog.IsGloballyEnabled(id), EnablementScope.Workspace));
		}

		return x;
	}

	private void StartReview() {
		_reviewing = true;
		_tab = Tab.Review;
		_scrollY = 0f;
		_layoutWidth = -1f;
		Repaint();
	}

	private float Button(IPainter painter, string label, float x, float top, bool primary, string key, Action action) {
		float scale = Scale;
		ITextFont font = painter.GetUi();
		float width = font.Measure(label) + (ButtonPadding * 2f * scale);
		float height = ButtonHeight * scale;
		bool hovered = _hoverKey == key;
		Color background = primary ? hovered ? Theme.ButtonHoverBackground : Theme.ButtonBackground : hovered ? Theme.ButtonSecondaryHoverBackground : Theme.ButtonSecondaryBackground;
		Color foreground = primary ? Theme.ButtonForeground : Theme.ButtonSecondaryForeground;

		painter.FillRoundedRectangle(ScreenX + x, ScreenY + top, width, height, Metrics.RadiusSmall * scale, background);
		painter.DrawText(font, label, ScreenX + x + (ButtonPadding * scale), ScreenY + top + ((height + font.GetAscent() - font.GetDescent()) / 2f), foreground);
		_hits.Add(new HitBox(x, top, width, height, action, key));
		return x + width;
	}

	private void PaintChips(IPainter painter, ExtensionEntry entry, bool enabled, float x, float top) {
		float scale = Scale;
		float chipTop = top;

		if (_catalog.LastError(entry.Id) is { } error) x = Chip(painter, Codicon.Error, error, x, chipTop, Theme.ErrorForeground);

		if (entry.Kind == ExtensionKind.Marketplace) {
			if (entry.Installs > 0) x = Chip(painter, Codicon.CloudDownload, ExtensionsView.FormatCount(entry.Installs) + " installs", x, chipTop, Theme.DescriptionForeground);

			if (entry.RatingCount > 0) x = Chip(painter, Codicon.Star, $"{entry.Rating:0.0} ({entry.RatingCount})", x, chipTop, Theme.DescriptionForeground);

			if (entry.LastUpdated is { } updated) x = Chip(painter, Codicon.Clock, "Updated " + updated.ToString("d MMM yyyy", CultureInfo.InvariantCulture), x, chipTop, Theme.DescriptionForeground);

			Chip(painter, entry.Verified ? Codicon.VerifiedFilled : Codicon.Warning, entry.Verified ? "Verified publisher" : "Unverified publisher", x, chipTop, entry.Verified ? Theme.TextLinkForeground : Theme.WarningForeground);
			return;
		}

		if (_catalog.AvailableUpdate(entry.Id) is { } update) x = Chip(painter, Codicon.ArrowUp, $"{update.Version} available", x, chipTop, Theme.TextLinkForeground);

		if (_catalog.NeedsRestart(entry.Id)) x = Chip(painter, Codicon.Warning, "Restart to activate", x, chipTop, Theme.WarningForeground);

		if (!enabled) x = Chip(painter, Codicon.CircleSlash, _catalog.WorkspaceOverride(entry.Id) is false ? "Disabled in this workspace" : "Disabled", x, chipTop, Theme.DescriptionForeground);
		else if (_catalog.ActivationTime(entry.Id) is { } time) x = Chip(painter, Codicon.Zap, "Activated in " + ExtensionsView.FormatTime(time), x, chipTop, Theme.SuccessForeground);
		else x = Chip(painter, Codicon.Clock, _catalog.IsActive(entry.Id) ? "Active" : "Not yet activated", x, chipTop, Theme.DescriptionForeground);

		if (_catalog.HostMemory(entry.Id) is { } memory) x = Chip(painter, Codicon.Server, $"Host {memory / (1024 * 1024)} MB", x, chipTop, Theme.DescriptionForeground);

		Chip(painter, entry.Kind == ExtensionKind.BuiltIn ? Codicon.Package : Codicon.Extensions, entry.Kind == ExtensionKind.BuiltIn ? "Built-in" : "Installed", x, chipTop, Theme.DescriptionForeground);
	}

	private float Chip(IPainter painter, Codicon icon, string text, float x, float top, Color color) {
		float scale = Scale;
		ITextFont font = painter.GetUiSmall();
		float height = ChipHeight * scale;
		float width = font.Measure(text) + (28f * scale);

		painter.FillRoundedRectangle(ScreenX + x, ScreenY + top, width, height, height / 2f, color.Transparency(0.12f));
		painter.DrawIcon(icon, ScreenX + x + (11f * scale), ScreenY + top + (height / 2f), 12f * scale, color);
		painter.DrawText(font, text, ScreenX + x + (20f * scale), ScreenY + top + ((height + font.GetAscent() - font.GetDescent()) / 2f), color);
		return x + width + (6f * scale);
	}

	private void PaintTabs(IPainter painter, float left, float top) {
		float scale = Scale;
		ITextFont font = painter.GetUi();
		float height = TabHeight * scale;
		float x = left;

		painter.FillRectangle(ScreenX + left, ScreenY + top + height - scale, FrameWidth, MathF.Max(1f, scale), Theme.PanelBorder);

		foreach (Tab tab in Tabs) {
			string label = tab switch { Tab.Features => "Features", Tab.Changelog => "Changelog", Tab.Review => "Review Before Installing", Tab.Permissions => "Permissions", _ => "Details" };
			float width = font.Measure(label) + (16f * scale);
			bool active = tab == _tab;
			string key = "tab." + label;
			Color color = active ? Theme.Foreground : _hoverKey == key ? Theme.Foreground.Transparency(0.85f) : Theme.DescriptionForeground;

			painter.DrawText(font, label, ScreenX + x + (8f * scale), ScreenY + top + ((height + font.GetAscent() - font.GetDescent()) / 2f), color);

			if (active) painter.FillRectangle(ScreenX + x, ScreenY + top + height - (2f * scale), width, 2f * scale, Theme.Accent);

			Tab target = tab;
			_hits.Add(new HitBox(x, top, width, height, () => {
				_tab = target;
				_scrollY = 0f;
				Repaint();
			}, key));
			x += width + (4f * scale);
		}
	}

	private void PaintBody(IPainter painter, float left) {
		if (_markdown is not { } markdown) return;

		float clipTop = ScreenY + BodyViewTop;
		float originX = ScreenX + left;
		float originY = clipTop - _scrollY;

		painter.PushClip(ScreenX, clipTop, Wide, MathF.Max(0f, Tall - BodyViewTop));

		if (_tab == Tab.Permissions && Entry is { } entry) PaintPermissions(painter, entry, left, BodyViewTop - _scrollY);

		foreach (HoverMarkdownLayout.BoxDraw box in markdown.Boxes) painter.FillRoundedRectangle(originX + box.X, originY + box.Y, box.Width, box.Height, box.Radius, box.Color);

		_selection.Paint(painter, originX, originY, clipTop, ScreenY + Tall, HasFocus);

		foreach (HoverMarkdownLayout.LinkText link in markdown.LinkTexts) painter.DrawText(link.Font, link.Text, originX + link.X, originY + link.Y + link.Font.GetBaseline(), link.Href == _hoverHref ? Theme.TextLinkActiveForeground : Theme.TextLinkForeground);

		foreach (HoverMarkdownLayout.IconDraw icon in markdown.Icons) painter.DrawIcon(icon.Icon, originX + icon.X + (icon.Size / 2f), originY + icon.Y + (icon.Size / 2f), icon.Size, icon.Href is not null && icon.Href == _hoverHref ? Theme.TextLinkActiveForeground : icon.Color);

		foreach (HoverMarkdownLayout.ImageDraw image in markdown.Images) painter.DrawImage(image.Image, originX + image.X, originY + image.Y, image.Width, image.Height, false);

		foreach (HoverMarkdownLayout.RuleDraw rule in markdown.Rules) painter.FillRectangle(originX, originY + rule.Y, FrameWidth, MathF.Max(1f, Scale), Theme.PanelBorder);

		painter.PopClip();
	}

	private void PaintScrollbar(IPainter painter) {
		float visible = Tall - BodyViewTop;

		if (MaxScroll <= 0f || visible <= 0f) return;

		float scale = Scale;
		float content = visible + MaxScroll;
		float thumb = MathF.Max(20f * scale, visible * visible / content);
		float y = ScreenY + BodyViewTop + ((visible - thumb) * (_scrollY / MaxScroll));
		painter.FillRectangle(ScreenX + Wide - (ScrollbarWidth * scale), y, ScrollbarWidth * scale, thumb, Theme.ScrollbarSlider);
	}
}
