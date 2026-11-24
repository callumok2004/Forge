using System.Text.Json;
using System.Text.Json.Nodes;

using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.UI.Layout;

namespace Forge.Workbench;

public sealed class SourceControlView : Panel
{
	private readonly HashSet<string> _collapsed = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly List<RepositorySection> _sections = [];
	private readonly Dictionary<RepositorySection, SplitLayout.Item> _items = [];
	private readonly Dictionary<RepositorySection, float?> _expandedSizes = [];
	private readonly Dictionary<string, (bool Expanded, float Size)> _sectionStates = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<string> _visible = [];
	private readonly List<SectionSash> _sashes = [];
	private readonly SplitLayout _layout = new();
	private readonly Label _empty;

	private string? _statePath;
	private string? _sectionsPath;
	private List<string>? _previousAll;
	private List<string>? _previousVisible;
	private bool _didSelectRepository;
	private volatile bool _stale = true;

	public SourceControlView() {
		Name = "SourceControl";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_empty = new Label("This folder is not a git repository.") { Name = "ScmEmpty", Foreground = Theme.DescriptionForeground, Padding = new Thickness(Metrics.Space12 + Metrics.Space8, 0f, 0f, 0f) };
		AddChild(_empty);

		HeaderActions = [new ActionButton { Name = "ViewAndSort", Icon = Codicon.Ellipsis, Foreground = Theme.Foreground, Radius = Metrics.RadiusSmall, Action = OpenViewAndSort }];
	}

	public IReadOnlyList<ActionButton> HeaderActions { get; }

	public IGitRepositories? Repositories {
		get;
		set {
			if (ReferenceEquals(field, value)) return;

			field = value;
			value?.OnChanged(MarkStale);
			_stale = true;
		}
	}

	public IReadOnlyList<RepositorySection> Sections => _sections;

	public IReadOnlyList<string> VisibleRoots => _visible;

	public event Action? VisibilityChanged;

	public event Action? RepositoriesChanged;

	public string SelectionMode { get; set; } = "multiple";

	public string SortOrder {
		get;
		set {
			if (field == value) return;

			field = value;
			_stale = true;
		}
	} = "discovery time";

	public IReadOnlyList<IGitRepository> Ordered() {
		IReadOnlyList<IGitRepository> all = Repositories?.GetAll() ?? [];

		return SortOrder switch {
			"name" => [.. all.OrderBy(LabelOf, StringComparer.OrdinalIgnoreCase).ThenBy(static repository => repository.GetRootPath(), StringComparer.OrdinalIgnoreCase)],
			"path" => [.. all.OrderBy(static repository => repository.GetRootPath(), StringComparer.OrdinalIgnoreCase)],
			_ => all,
		};
	}

	public bool TreeView {
		get;
		set {
			if (field == value) return;

			field = value;

			foreach (RepositorySection section in _sections) section.Tree.Invalidate();

			Repaint();
		}
	} = true;

	public Action<string>? FileOpened { get; set; }

	public Action<GitEntry, bool>? ChangeOpened { get; set; }

	public Action<IGitRepository, Func<IGitRepository, bool>>? RunGit { get; set; }

	public Action<string, IGitRepository>? CommandRequested { get; set; }

	public Action<IGitRepository>? RepositoryFocused { get; set; }

	public void RunCommand(string command, IGitRepository repository) {
		RepositoryFocused?.Invoke(repository);
		CommandRequested?.Invoke(command, repository);
	}

	public void Run(IGitRepository repository, Func<IGitRepository, bool> command) => RunGit?.Invoke(repository, command);

	public RepositorySection? SectionFor(IGitRepository repository) => _sections.Find(section => ReferenceEquals(section.Repository, repository));

	public TextEntry? InputFor(IGitRepository repository) => SectionFor(repository)?.Tree.Input;

	public string MessageFor(IGitRepository repository) => SectionFor(repository)?.Tree.Message ?? string.Empty;

	public void ClearMessage(IGitRepository repository) => InputFor(repository)?.Clear();

	public IGitRepository? FocusedInputRepository() => _sections.Find(static section => section.Tree.Input.HasFocus)?.Repository;

	public bool IsCommitting(IGitRepository repository) => SectionFor(repository)?.Tree.Committing ?? false;

	public void SetCommitting(IGitRepository repository, bool committing) {
		if (SectionFor(repository) is { } section) section.Tree.Committing = committing;

		bool any = _sections.Exists(static section => section.Tree.Committing);

		if (any == _progressing) return;

		_progressing = any;
		_progress = 0f;
		SetThinking(any);
		Repaint();
	}

	private bool _progressing;
	private float _progress;

	public override void OnThink(float deltaSeconds) {
		_progress = (_progress + (deltaSeconds / 4f)) % 1f;
		Repaint(0f, 0f, Wide, 2f * Scale);
	}

	public override void PostChildPaint(IPainter painter) {
		if (!_progressing) return;

		float width = Wide * 0.02f;
		painter.FillRectangle(ScreenX + (_progress * 50f * width), ScreenY, width, 2f * Scale, Theme.ProgressBarBackground);
	}

	public string ProviderCountBadge {
		get;
		set {
			if (field == value) return;

			field = value;
			_stale = true;
		}
	} = "hidden";

	public bool ShowsSelectedIcon => (Repositories?.GetAll().Count ?? 0) > 1;

	public string LabelOf(IGitRepository repository) => Path.GetFileName(repository.GetRootPath().TrimEnd(Path.DirectorySeparatorChar));

	public void MarkStale() => _stale = true;

	public bool IsVisible(IGitRepository repository) => _visible.Exists(root => string.Equals(root, repository.GetRootPath(), StringComparison.OrdinalIgnoreCase));

	public void ToggleVisibility(IGitRepository repository, bool? visible = null, bool single = false) {
		bool target = visible ?? !IsVisible(repository);

		if (target == IsVisible(repository) && !single) return;

		string root = repository.GetRootPath();

		if (target) {
			if (single) _visible.Clear();

			if (!_visible.Contains(root, StringComparer.OrdinalIgnoreCase)) _visible.Add(root);
		}
		else _visible.RemoveAll(existing => string.Equals(existing, root, StringComparison.OrdinalIgnoreCase));

		_didSelectRepository = true;
		SaveSections();
		_stale = true;
		VisibilityChanged?.Invoke();
		InvalidateLayout();
		Repaint();
	}

	public void SetVisible(IReadOnlyList<IGitRepository> repositories) {
		_visible.Clear();

		foreach (IGitRepository repository in repositories) _visible.Add(repository.GetRootPath());

		_didSelectRepository = true;
		SaveSections();
		_stale = true;
		VisibilityChanged?.Invoke();
		InvalidateLayout();
		Repaint();
	}

	public void SyncIfStale() {
		if (!_stale) return;

		_stale = false;

		IReadOnlyList<IGitRepository> all = Repositories?.GetAll() ?? [];

		UpdateVisible(all);
		UpdateSections(all);

		_empty.Visible = all.Count == 0;

		foreach (RepositorySection section in _sections) section.Sync();

		RepositoriesChanged?.Invoke();
		InvalidateLayout();
		Repaint();
	}

	private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

	private void UpdateVisible(IReadOnlyList<IGitRepository> all) {
		_visible.RemoveAll(root => !all.Any(repository => string.Equals(repository.GetRootPath(), root, StringComparison.OrdinalIgnoreCase)));

		foreach (IGitRepository repository in all) {
			string root = repository.GetRootPath();

			if (!_known.Add(root)) continue;

			repository.OnChanged(MarkStale);

			if (_previousAll is { } previousAll && !_didSelectRepository) {
				int index = previousAll.FindIndex(existing => string.Equals(existing, root, StringComparison.OrdinalIgnoreCase));

				if (index >= 0 && _previousVisible is { } previousVisible && !previousVisible.Contains(root, StringComparer.OrdinalIgnoreCase)) continue;
			}

			_visible.Add(root);
		}

		if (_visible.Count == 0 && all.Count > 0) _visible.Add(all[0].GetRootPath());

		if (_previousVisible is { } wanted) {
			List<string> ordered = [.. wanted.Where(root => _visible.Contains(root, StringComparer.OrdinalIgnoreCase))];

			foreach (string root in _visible) {
				if (!ordered.Contains(root, StringComparer.OrdinalIgnoreCase)) ordered.Add(root);
			}

			_visible.Clear();
			_visible.AddRange(ordered);
		}

		if (SortOrder is "name" or "path") {
			List<string> sorted = [.. Ordered().Select(static repository => repository.GetRootPath()).Where(root => _visible.Contains(root, StringComparer.OrdinalIgnoreCase))];
			_visible.Clear();
			_visible.AddRange(sorted);
		}
	}

	private void UpdateSections(IReadOnlyList<IGitRepository> all) {
		for (int i = _sections.Count - 1; i >= 0; i--) {
			RepositorySection section = _sections[i];

			if (IsVisible(section.Repository) && all.Contains(section.Repository)) continue;

			RememberSection(section);
			_layout.Remove(i, distribute: section.Expanded);
			_items.Remove(section);
			_expandedSizes.Remove(section);
			_sections.RemoveAt(i);
			RemoveChild(section);
		}

		for (int index = 0; index < _visible.Count; index++) {
			string root = _visible[index];
			IGitRepository? repository = all.FirstOrDefault(candidate => string.Equals(candidate.GetRootPath(), root, StringComparison.OrdinalIgnoreCase));

			if (repository is null || SectionFor(repository) is not null) continue;

			(bool expanded, float size) = _sectionStates.TryGetValue(root, out (bool Expanded, float Size) state) ? state : (true, RepositorySection.DefaultSize);
			RepositorySection section = new(this, repository, expanded, size * Scale);
			int position = Math.Min(Math.Max(index, 0), _sections.Count);

			_sections.Insert(position, section);
			_expandedSizes[section] = null;
			AddChild(section);
			_items[section] = _layout.Add(section.Size, () => Minimum(section), () => Maximum(section), position);
		}
	}

	private float HeaderSize => RepositoryHeader.HeightFor(Scale);

	private float Minimum(RepositorySection section) => HeaderSize + (section.Expanded ? RepositorySection.MinimumBodySize * Scale : 0f);

	private float Maximum(RepositorySection section) => section.Expanded ? float.PositiveInfinity : HeaderSize;

	public void SectionsChanged() {
		foreach (RepositorySection section in _sections) {
			if (!_items.TryGetValue(section, out SplitLayout.Item? item)) continue;

			bool open = item.Size > HeaderSize + 0.5f;

			if (section.Expanded == open) continue;

			if (!section.Expanded) _expandedSizes[section] = item.Size;

			_layout.ViewChanged(item, section.Expanded ? _expandedSizes.GetValueOrDefault(section) : null);
		}

		SaveSections();
		InvalidateLayout();
		Repaint();
	}

	private void RememberSection(RepositorySection section) {
		float size = _items.TryGetValue(section, out SplitLayout.Item? item) ? item.Size : -1f;
		float previous = _sectionStates.TryGetValue(section.Key, out (bool Expanded, float Size) state) ? state.Size : RepositorySection.DefaultSize;

		_sectionStates[section.Key] = (section.Expanded, section.Expanded && size > 0f ? size / Scale : previous);
	}

	public void SaveState() => SaveSections();

	public override void PerformLayout() {
		float scale = Scale;

		_empty.SetBounds(0f, 0f, Wide, Metrics.TreeRowHeight * scale * 1.5f);

		if (_layout.Size != Tall) _layout.Layout(Tall);

		float y = 0f;

		for (int i = 0; i < _sections.Count; i++) {
			RepositorySection section = _sections[i];
			float size = _items[section].Size;

			section.SetBounds(0f, y, Wide, size);

			if (section.Expanded) _expandedSizes[section] = size;

			y += size;
		}

		LayoutSashes(scale);
	}

	private void LayoutSashes(float scale) {
		int needed = Math.Max(0, _sections.Count - 1);

		while (_sashes.Count < needed) {
			SectionSash sash = new(this, _sashes.Count);
			_sashes.Add(sash);
			AddChild(sash);
		}

		float size = SectionSash.Size * scale;

		for (int i = 0; i < _sashes.Count; i++) {
			SectionSash sash = _sashes[i];
			sash.Visible = i < needed;

			if (!sash.Visible) continue;

			float boundary = _sections[i + 1].Y;
			sash.Enabled = _layout.CanResize(i);
			sash.Cursor = sash.Enabled ? CursorCode.SizeNorthSouth : CursorCode.Arrow;
			sash.SetBounds(0f, boundary - (size / 2f), Wide, size);
			sash.MoveToFront();
		}
	}

	private void SashStarted(int index, float position) => _layout.SashStart(index, position);

	private void SashMoved(float position) {
		_layout.SashChange(position);
		InvalidateLayout();
		Repaint();
	}

	private void SashEnded() {
		_layout.SashEnd();
		SaveSections();
	}

	private sealed class SectionSash(SourceControlView owner, int index) : Panel
	{
		public const float Size = 4f;

		private bool _dragging;

		public override void OnCursorEntered() => Repaint();

		public override void OnCursorExited() => Repaint();

		public override bool OnMousePressed(MouseButton button, float x, float y) {
			if (button != MouseButton.Left || !Enabled) return false;

			_dragging = true;
			owner.SashStarted(index, GetAbsPos().Y + y);
			Ui?.SetMouseCapture(this);
			Repaint();
			return true;
		}

		public override void OnCursorMoved(float x, float y) {
			if (_dragging) owner.SashMoved(GetAbsPos().Y + y);
		}

		public override bool OnMouseReleased(MouseButton button, float x, float y) {
			Finish();

			if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

			return true;
		}

		public override void OnMouseCaptureLost() => Finish();

		private void Finish() {
			if (!_dragging) return;

			_dragging = false;
			owner.SashEnded();
			Repaint();
		}

		public override void Paint(IPainter painter) {
			if (Enabled && (_dragging || IsCursorOver)) painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.FocusBorder);
		}
	}

	public bool IsCollapsed(string key) => _collapsed.Contains(key);

	public void Toggle(string key) {
		if (!_collapsed.Remove(key)) _collapsed.Add(key);

		SaveState(_statePath, _collapsed);
	}

	public void LoadState(string path, string sectionsPath) {
		_statePath = path;
		_sectionsPath = sectionsPath;

		try {
			if (File.Exists(path)) {
				foreach (string line in File.ReadAllLines(path))
					if (line.Length > 0) _collapsed.Add(line);
			}

			if (File.Exists(sectionsPath) && JsonNode.Parse(File.ReadAllText(sectionsPath)) is JsonObject root) {
				if (root["sections"] is JsonObject sections) {
					foreach ((string key, JsonNode? value) in sections) {
						if (value is not JsonObject state) continue;

						_sectionStates[key] = (state["expanded"]?.GetValue<bool>() ?? true, state["size"]?.GetValue<float>() ?? RepositorySection.DefaultSize);
					}
				}

				if (root["all"] is JsonArray all && root["visible"] is JsonArray visible) {
					_previousAll = [.. all.Select(static node => node?.GetValue<string>() ?? string.Empty)];
					_previousVisible = [.. visible.Select(node => node?.GetValue<int>() ?? -1).Where(index => index >= 0 && index < _previousAll.Count).Select(index => _previousAll[index])];
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
		}
	}

	private void SaveSections() {
		if (_sectionsPath is null) return;

		foreach (RepositorySection section in _sections) RememberSection(section);

		JsonObject sections = [];

		foreach ((string key, (bool expanded, float size)) in _sectionStates) sections[key] = new JsonObject { ["expanded"] = expanded, ["size"] = size };

		List<string> all = [.. (Repositories?.GetAll() ?? []).Select(static repository => repository.GetRootPath())];
		JsonArray visible = [.. _visible.Select(root => (JsonNode)all.FindIndex(existing => string.Equals(existing, root, StringComparison.OrdinalIgnoreCase))).Where(static node => node.GetValue<int>() >= 0)];

		JsonObject state = new() {
			["sections"] = sections,
			["all"] = new JsonArray([.. all.Select(static root => (JsonNode)root)]),
			["visible"] = visible,
		};

		_previousAll = all;
		_previousVisible = [.. _visible];

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(_sectionsPath)!);
			File.WriteAllText(_sectionsPath, state.ToJsonString());
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private static void SaveState(string? path, HashSet<string> collapsed) {
		if (path is null) return;

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllLines(path, collapsed);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private void OpenViewAndSort() {
		ActionButton button = HeaderActions[0];
		(float x, float y) = button.GetAbsPos();
		ApplicationMenu.Build(GitMenus.ViewAndSort).Open(x, y + button.Tall);
	}
}
