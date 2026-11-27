using System.Globalization;

using Forge.Common.Extensions;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ExtensionsView : Panel
{
	private const float SearchTop = 6f;
	private const float SearchHeight = 26f;
	private const float SidePadding = 12f;
	private const float SectionHeight = 26f;
	private const float RowHeight = 46f;
	private const float IconTile = 30f;
	private const float ScrollbarWidth = 10f;

	private enum RowKind : byte
	{
		Section,
		Extension,
		Message,
	}

	private readonly record struct Row(RowKind Kind, string Text, ExtensionEntry? Entry, int Count);

	private readonly TextEntry _search;
	private readonly Menu _menu = new();
	private readonly List<Row> _rows = [];
	private readonly HashSet<string> _collapsed = [with(StringComparer.Ordinal)];
	private readonly Dictionary<string, IImage?> _icons = [with(StringComparer.OrdinalIgnoreCase)];

	private float _scrollY;
	private int _hover = -1;
	private string? _selected;
	private CancellationTokenSource? _searchCancel;
	private List<ExtensionEntry> _marketplace = [];
	private string _marketplaceQuery = string.Empty;
	private bool _searching;

	public ExtensionsView() {
		Name = "ExtensionsView";
		Dock = DockType.Fill;
		KeyboardInputEnabled = true;

		_search = new TextEntry { Name = "ExtensionsSearch", Placeholder = "Search Marketplace  (@installed, @builtin, @updates, @disabled)", FontSize = 13f, TextChanged = QueryChanged };
		AddChild(_search);
	}

	public IExtensionCatalog? Catalog {
		get;
		set {
			if (field is not null) field.Changed -= Rebuild;

			field = value;

			if (value is not null) value.Changed += Rebuild;

			Rebuild();
		}
	}

	public Func<string, IImage?>? ImageLoader { get; set; }

	public Action<string>? OpenDetails { get; set; }

	public Action<Action>? Post { get; set; }

	private static readonly string[] Filters = ["@builtin", "@installed", "@enabled", "@disabled", "@updates"];

	private void QueryChanged() {
		Rebuild();

		string text = string.Join(' ', _search.GetString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(static word => !Filters.Contains(word, StringComparer.OrdinalIgnoreCase)));
		bool localOnly = _search.GetString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(static word => Filters.Contains(word, StringComparer.OrdinalIgnoreCase));

		_searchCancel?.Cancel();
		_searchCancel = null;

		if (text.Length == 0 || localOnly || Catalog is not { } catalog || Post is not { } post) {
			_marketplace = [];
			_marketplaceQuery = string.Empty;
			_searching = false;
			Rebuild();
			return;
		}

		CancellationTokenSource cancel = new();
		_searchCancel = cancel;
		_searching = true;
		Rebuild();
		_ = SearchAsync(catalog, post, text, cancel.Token);
	}

	private async Task SearchAsync(IExtensionCatalog catalog, Action<Action> post, string text, CancellationToken cancellation) {
		try {
			await Task.Delay(300, cancellation).ConfigureAwait(false);
			IReadOnlyList<ExtensionEntry> results = await catalog.SearchAsync(text, cancellation).ConfigureAwait(false);

			if (cancellation.IsCancellationRequested) return;

			post(() => {
				if (cancellation.IsCancellationRequested) return;

				_marketplace = [.. results];
				_marketplaceQuery = text;
				_searching = false;
				Rebuild();
			});
		}
		catch (TaskCanceledException) {
		}
	}

	public void FocusSearch() => _search.RequestFocus();

	private float ListTop => (SearchTop + SearchHeight + SearchTop) * Scale;

	private float ListHeight => MathF.Max(0f, Tall - ListTop);

	private float ContentHeight {
		get {
			float total = 0f;

			foreach (Row row in _rows) total += HeightOf(row);

			return total;
		}
	}

	private float MaxScroll => MathF.Max(0f, ContentHeight - ListHeight);

	private float HeightOf(in Row row) => (row.Kind == RowKind.Extension ? RowHeight : SectionHeight) * Scale;

	public void Rebuild() {
		_rows.Clear();

		if (Catalog is not { } catalog) {
			Repaint();
			return;
		}

		string query = _search.GetString().Trim();
		bool builtInOnly = false, installedOnly = false, enabledOnly = false, disabledOnly = false, updatesOnly = false;
		List<string> words = [];

		foreach (string word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
			switch (word.ToLowerInvariant()) {
				case "@builtin": builtInOnly = true; break;
				case "@installed": installedOnly = true; break;
				case "@enabled": enabledOnly = true; break;
				case "@disabled": disabledOnly = true; break;
				case "@updates": updatesOnly = true; break;
				default: words.Add(word); break;
			}
		}

		List<ExtensionEntry> matches = [.. catalog.All().Where(entry => (!builtInOnly || entry.Kind == ExtensionKind.BuiltIn) && (!installedOnly || entry.Kind == ExtensionKind.Installed) && (!enabledOnly || catalog.IsEnabled(entry.Id)) && (!disabledOnly || !catalog.IsEnabled(entry.Id)) && (!updatesOnly || catalog.AvailableUpdate(entry.Id) is not null) && words.TrueForAll(word => Matches(entry, word)))];

		AddSection("Installed", [.. matches.Where(static entry => entry.Kind == ExtensionKind.Installed)], catalog, query.Length == 0 ? "No extensions installed yet" : null);
		AddSection("Built-in", [.. matches.Where(static entry => entry.Kind == ExtensionKind.BuiltIn)], catalog, null);

		bool remote = _searching || _marketplaceQuery.Length > 0;

		if (remote) {
			List<ExtensionEntry> gallery = [.. _marketplace.Where(entry => catalog.All().All(local => !string.Equals(local.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))];
			_rows.Add(new Row(RowKind.Section, "Marketplace", null, gallery.Count));

			if (!_collapsed.Contains("Marketplace")) {
				if (_searching) _rows.Add(new Row(RowKind.Message, "Searching the marketplace…", null, 0));
				else if (gallery.Count == 0) _rows.Add(new Row(RowKind.Message, "No marketplace results", null, 0));
				else foreach (ExtensionEntry entry in gallery) _rows.Add(new Row(RowKind.Extension, entry.DisplayName, catalog.Find(entry.Id) ?? entry, 0));
			}
		}
		else if (matches.Count == 0 && query.Length > 0) _rows.Add(new Row(RowKind.Message, "No extensions found", null, 0));

		_scrollY = Math.Clamp(_scrollY, 0f, MaxScroll);
		_hover = -1;
		Repaint();
	}

	private static bool Matches(ExtensionEntry entry, string word) => entry.DisplayName.Contains(word, StringComparison.OrdinalIgnoreCase) || entry.Id.Contains(word, StringComparison.OrdinalIgnoreCase) || entry.Description.Contains(word, StringComparison.OrdinalIgnoreCase) || entry.Publisher.Contains(word, StringComparison.OrdinalIgnoreCase);

	private void AddSection(string title, List<ExtensionEntry> entries, IExtensionCatalog catalog, string? empty) {
		if (entries.Count == 0 && empty is null) return;

		_rows.Add(new Row(RowKind.Section, title, null, entries.Count));

		if (_collapsed.Contains(title)) return;

		if (entries.Count == 0) {
			_rows.Add(new Row(RowKind.Message, empty!, null, 0));
			return;
		}

		entries.Sort((left, right) => {
			int enabled = catalog.IsEnabled(right.Id).CompareTo(catalog.IsEnabled(left.Id));
			return enabled != 0 ? enabled : string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
		});

		foreach (ExtensionEntry entry in entries) _rows.Add(new Row(RowKind.Extension, entry.DisplayName, entry, 0));
	}

	public override void PerformLayout() {
		float scale = Scale;
		_search.SetBounds(SidePadding * scale, SearchTop * scale, MathF.Max(0f, Wide - (SidePadding * 2f * scale)), SearchHeight * scale);
		base.PerformLayout();
	}

	private int RowAt(float y, out float top) {
		top = ListTop - _scrollY;

		if (y < ListTop) return -1;

		for (int i = 0; i < _rows.Count; i++) {
			float height = HeightOf(_rows[i]);

			if (y >= top && y < top + height) return i;

			top += height;
		}

		return -1;
	}

	public override void OnCursorMoved(float x, float y) {
		int row = RowAt(y, out _);

		if (row == _hover) return;

		_hover = row;
		Repaint();
	}

	public override void OnCursorExited() {
		_hover = -1;
		Repaint();
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float previous = _scrollY;
		_scrollY = Math.Clamp(_scrollY - (delta / 120f * RowHeight * Scale), 0f, MaxScroll);

		if (_scrollY != previous) Repaint();

		return true;
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		int index = RowAt(y, out _);

		if (index < 0 || Catalog is not { } catalog) return true;

		Row row = _rows[index];

		if (row.Kind == RowKind.Section) {
			if (button == MouseButton.Left && !_collapsed.Remove(row.Text)) _collapsed.Add(row.Text);

			Rebuild();
			return true;
		}

		if (row.Entry is not { } entry) return true;

		_selected = entry.Id;
		Repaint();

		if (button == MouseButton.Right) {
			OpenMenu(entry, catalog, x, y);
			return true;
		}

		OpenDetails?.Invoke(entry.Id);
		return true;
	}

	private void OpenMenu(ExtensionEntry entry, IExtensionCatalog catalog, float x, float y) {
		string id = entry.Id;
		bool enabled = catalog.IsEnabled(id);

		_menu.Clear();
		_menu.Add(new MenuItem("Open Details", null, () => OpenDetails?.Invoke(id)));

		if (entry.Kind == ExtensionKind.Marketplace) {
			(float popupX, float popupY) = GetAbsPos();
			_menu.Open(popupX + x, popupY + y);
			return;
		}

		if (entry.Kind == ExtensionKind.Installed) {
			_menu.Add(new MenuItem("Check for Update", null, () => _ = catalog.CheckForUpdatesAsync(id)));

			if (catalog.AvailableUpdate(id) is { } update) _menu.Add(new MenuItem($"Update to {update.Version}", null, () => _ = catalog.InstallAsync(update)));

			_menu.Add(new MenuItem("Uninstall", null, () => _ = catalog.UninstallAsync(id)));
		}

		_menu.AddSeparator();
		_menu.Add(new MenuItem(enabled ? "Disable" : "Enable", null, () => catalog.SetEnabled(id, !enabled, EnablementScope.Global)));

		if (catalog.HasWorkspace) _menu.Add(new MenuItem(enabled ? "Disable (Workspace)" : "Enable (Workspace)", null, () => catalog.SetEnabled(id, !enabled, EnablementScope.Workspace)));

		(float absX, float absY) = GetAbsPos();
		_menu.Open(absX + x, absY + y);
	}

	public override void Paint(IPainter painter) {
		if (Catalog is not { } catalog) return;

		float scale = Scale;
		float top = ScreenY + ListTop - _scrollY;
		float clipTop = ScreenY + ListTop;

		painter.PushClip(ScreenX, clipTop, Wide, ListHeight);

		for (int i = 0; i < _rows.Count; i++) {
			Row row = _rows[i];
			float height = HeightOf(row);

			if (top + height >= clipTop && top <= clipTop + ListHeight) {
				switch (row.Kind) {
					case RowKind.Section:
						PaintSection(painter, row, top, height, i == _hover);
						break;
					case RowKind.Message:
						painter.DrawText(painter.GetUi(), row.Text, ScreenX + (SidePadding * 2f * scale), top + (height / 2f) + (painter.GetUi().GetAscent() / 2.6f), Theme.DescriptionForeground);
						break;
					default:
						PaintExtension(painter, row.Entry!, catalog, top, height, i == _hover);
						break;
				}
			}

			top += height;
		}

		painter.PopClip();

		PaintScrollbar(painter, clipTop);
	}

	private void PaintSection(IPainter painter, in Row row, float top, float height, bool hovered) {
		float scale = Scale;
		ITextFont font = painter.GetUiStrong();
		bool collapsed = _collapsed.Contains(row.Text);
		float x = ScreenX + (SidePadding * scale);

		painter.DrawChevron(x + (5f * scale), top + (height / 2f), 8f * scale, !collapsed, hovered ? Theme.Foreground : Theme.SectionHeaderForeground);

		string title = row.Text.ToUpperInvariant();
		float baseline = top + (height / 2f) + ((font.GetAscent() - font.GetDescent()) / 2f);
		painter.DrawText(font, title, x + (16f * scale), baseline, Theme.SectionHeaderForeground);

		string count = row.Count.ToString(CultureInfo.InvariantCulture);
		ITextFont small = painter.GetUiSmall();
		float countWidth = small.Measure(count) + (12f * scale);
		float badgeHeight = 16f * scale;
		float badgeX = ScreenX + Wide - (SidePadding * scale) - countWidth;
		painter.FillRoundedRectangle(badgeX, top + ((height - badgeHeight) / 2f), countWidth, badgeHeight, badgeHeight / 2f, Theme.BadgeBackground);
		painter.DrawText(small, count, badgeX + (6f * scale), top + (height / 2f) + ((small.GetAscent() - small.GetDescent()) / 2f), Theme.BadgeForeground);
	}

	private void PaintExtension(IPainter painter, ExtensionEntry entry, IExtensionCatalog catalog, float top, float height, bool hovered) {
		float scale = Scale;
		bool enabled = catalog.IsEnabled(entry.Id);
		bool selected = string.Equals(_selected, entry.Id, StringComparison.OrdinalIgnoreCase);
		float inset = Metrics.Space4 * scale;

		if (selected) painter.FillRoundedRectangle(ScreenX + inset, top + (1f * scale), Wide - (inset * 2f), height - (2f * scale), Metrics.RadiusSmall * scale, Theme.ListInactiveSelectionBackground);
		else if (hovered) painter.FillRoundedRectangle(ScreenX + inset, top + (1f * scale), Wide - (inset * 2f), height - (2f * scale), Metrics.RadiusSmall * scale, Theme.ListHoverBackground);

		float left = ScreenX + (SidePadding * scale);
		float tile = IconTile * scale;
		float tileTop = top + ((height - tile) / 2f);
		float opacity = enabled ? 1f : 0.45f;

		if (Icon(entry) is { } image) painter.DrawImage(image, left, tileTop, tile, tile, false);
		else {
			painter.FillRoundedRectangle(left, tileTop, tile, tile, Metrics.RadiusSmall * scale, (entry.Kind == ExtensionKind.BuiltIn ? Theme.Accent : Theme.ButtonSecondaryBackground).Transparency(enabled ? 0.22f : 0.1f));
			painter.DrawIcon(entry.Kind == ExtensionKind.BuiltIn ? Codicon.Gear : Codicon.Extensions, left + (tile / 2f), tileTop + (tile / 2f), 16f * scale, (entry.Kind == ExtensionKind.BuiltIn ? Theme.Accent : Theme.Foreground).Transparency(opacity));
		}

		float textLeft = left + tile + (10f * scale);
		float textRight = ScreenX + Wide - (SidePadding * scale);
		ITextFont name = painter.GetUiStrong();
		ITextFont small = painter.GetUiSmall();
		float firstBaseline = top + (9f * scale) + name.GetAscent();
		float secondBaseline = firstBaseline + (17f * scale);

		string version = entry.Version.Length > 0 ? entry.Version : string.Empty;
		float versionWidth = version.Length > 0 ? small.Measure(version) + (6f * scale) : 0f;
		string title = Fit(name, entry.DisplayName, MathF.Max(0f, textRight - textLeft - versionWidth));
		float titleWidth = name.Measure(title);

		painter.DrawText(name, title, textLeft, firstBaseline, (enabled ? Theme.Foreground : Theme.DisabledForeground));

		if (version.Length > 0 && textLeft + titleWidth + versionWidth <= textRight) painter.DrawText(small, version, textLeft + titleWidth + (6f * scale), firstBaseline, Theme.DescriptionForeground);

		string status = Status(entry, catalog, out Color statusColor);
		float statusWidth = status.Length > 0 ? small.Measure(status) : 0f;
		float descriptionRight = status.Length > 0 ? textRight - statusWidth - (8f * scale) : textRight;

		painter.DrawText(painter.GetUiDescription(), Fit(painter.GetUiDescription(), entry.Description.Length > 0 ? entry.Description : entry.Publisher, MathF.Max(0f, descriptionRight - textLeft)), textLeft, secondBaseline, Theme.DescriptionForeground.Transparency(enabled ? 1f : 0.7f));

		if (status.Length > 0) painter.DrawText(small, status, textRight - statusWidth, secondBaseline, statusColor);

	}

	private static string Status(ExtensionEntry entry, IExtensionCatalog catalog, out Color color) {
		color = Theme.DescriptionForeground;

		switch (catalog.Operation(entry.Id)) {
			case ExtensionOperation.Checking: return "checking…";
			case ExtensionOperation.Installing: return "installing…";
			case ExtensionOperation.Uninstalling: return "removing…";
		}

		if (catalog.LastError(entry.Id) is not null) {
			color = Theme.ErrorForeground;
			return "failed";
		}

		if (entry.Kind == ExtensionKind.Marketplace) return entry.Installs > 0 ? FormatCount(entry.Installs) + " installs" : string.Empty;

		if (catalog.AvailableUpdate(entry.Id) is { } update) {
			color = Theme.TextLinkForeground;
			return "update " + update.Version;
		}

		if (catalog.NeedsRestart(entry.Id)) {
			color = Theme.WarningForeground;
			return "restart needed";
		}

		bool? workspace = catalog.WorkspaceOverride(entry.Id);

		if (!catalog.IsEnabled(entry.Id)) return workspace is false ? "off in workspace" : "disabled";

		if (catalog.ActivationTime(entry.Id) is { } time) return FormatTime(time);

		if (workspace is true) return "on in workspace";

		return catalog.IsActive(entry.Id) ? "active" : "idle";
	}

	public static string FormatCount(long count) => count switch {
		>= 1_000_000 => $"{count / 1_000_000d:0.#}M",
		>= 1_000 => $"{count / 1_000d:0.#}K",
		_ => count.ToString(CultureInfo.InvariantCulture),
	};

	public static string FormatTime(TimeSpan time) => time.TotalMilliseconds >= 1000 ? $"{time.TotalSeconds:0.0} s" : $"{Math.Max(1, (int)Math.Round(time.TotalMilliseconds))} ms";

	private IImage? Icon(ExtensionEntry entry) {
		if (entry.IconPath is not { } path || ImageLoader is not { } loader) return null;

		if (!_icons.TryGetValue(path, out IImage? image)) _icons[path] = image = loader(path);

		return image;
	}

	private static string Fit(ITextFont font, string text, float width) {
		if (width <= 0f) return string.Empty;

		if (font.Measure(text) <= width) return text;

		float ellipsis = font.Measure("…");
		int length = text.Length;

		while (length > 0 && font.Measure(text.AsSpan(0, length)) + ellipsis > width) length--;

		return length == 0 ? string.Empty : string.Concat(text.AsSpan(0, length).TrimEnd(), "…");
	}

	private void PaintScrollbar(IPainter painter, float clipTop) {
		float content = ContentHeight;
		float visible = ListHeight;

		if (content <= visible || visible <= 0f) return;

		float scale = Scale;
		float width = ScrollbarWidth * scale;
		float thumb = MathF.Max(20f * scale, visible * visible / content);
		float y = clipTop + ((visible - thumb) * (_scrollY / MaxScroll));
		painter.FillRectangle(ScreenX + Wide - width, y, width, thumb, Theme.ScrollbarSlider);
	}
}
