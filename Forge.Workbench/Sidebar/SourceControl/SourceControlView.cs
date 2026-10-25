using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class SourceControlView : Panel
{
	private readonly HashSet<string> _collapsed = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly RepositorySection _section;
	private readonly Label _empty;

	private string? _statePath;
	private volatile bool _stale = true;

	public SourceControlView() {
		Name = "SourceControl";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_section = new RepositorySection(this) { Visible = false };
		_empty = new Label("This folder is not a git repository.") { Name = "ScmEmpty", Dock = DockType.Top, Foreground = Theme.DescriptionForeground, Padding = new Thickness(Metrics.Space12 + Metrics.Space8, 0f, 0f, 0f) };

		AddChild(_section);
		AddChild(_empty);

		HeaderActions = [new ActionButton { Name = "ViewAndSort", Icon = Codicon.Ellipsis, Foreground = Theme.Foreground, Radius = Metrics.RadiusSmall, Action = OpenViewAndSort }];
	}

	public IReadOnlyList<ActionButton> HeaderActions { get; }

	public IGitRepository? Repository {
		get;
		set {
			if (ReferenceEquals(field, value)) return;

			field = value;
			value?.OnChanged(() => _stale = true);
			_stale = true;
		}
	}

	public bool TreeView {
		get;
		set {
			if (field == value) return;

			field = value;
			_section.Tree.Invalidate();
			Repaint();
		}
	} = true;

	public string RootPath { get; private set; } = string.Empty;

	public Action<string>? FileOpened { get; set; }

	public Action<Func<IGitRepository, bool>>? RunGit { get; set; }

	public TextEntry Input => _section.Tree.Input;

	public string Message => _section.Tree.Message;

	public void ClearMessage() => Input.Clear();

	public void Run(Func<IGitRepository, bool> command) => RunGit?.Invoke(command);

	public void SetRoot(string rootPath) {
		RootPath = rootPath;
		_stale = true;
	}

	public void MarkStale() => _stale = true;

	public void SyncIfStale() {
		if (!_stale) return;

		_stale = false;

		bool hasRepository = Repository is not null;
		_section.Visible = hasRepository;
		_empty.Visible = !hasRepository;

		if (Repository is { } repository) _section.Sync(repository);
	}

	public string TrimRoot(string folder) => RootPath.Length > 0 && folder.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase) ? folder[RootPath.Length..].TrimStart(Path.DirectorySeparatorChar) : folder;

	public bool IsCollapsed(string key) => _collapsed.Contains(key);

	public void Toggle(string key) {
		if (!_collapsed.Remove(key)) _collapsed.Add(key);

		SaveState();
	}

	public void LoadState(string path) {
		_statePath = path;

		try {
			if (!File.Exists(path)) return;

			foreach (string line in File.ReadAllLines(path))
				if (line.Length > 0) _collapsed.Add(line);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private void SaveState() {
		if (_statePath is null) return;

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
			File.WriteAllLines(_statePath, _collapsed);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private void OpenViewAndSort() {
		ActionButton button = HeaderActions[0];
		(float x, float y) = button.GetAbsPos();
		ApplicationMenu.Build(GitMenus.ViewAndSort).Open(x, y + button.Tall);
	}

	public override void PerformLayout() {
		_empty.SetSize(Wide, Metrics.TreeRowHeight * Scale * 1.5f);
		base.PerformLayout();
	}
}
