using Forge.Common.Git;
using Forge.Common.UI;

namespace Forge.Workbench;

public sealed class RepositorySection : Panel
{
	public const float DefaultSize = 220f;
	public const float MinimumBodySize = 66f;

	private readonly SourceControlView _view;

	public RepositorySection(SourceControlView view, IGitRepository repository, bool expanded, float size) {
		_view = view;
		Repository = repository;
		Expanded = expanded;
		Size = size;
		Name = "RepositorySection";
		MouseInputEnabled = false;

		Header = new RepositoryHeader(this);
		Tree = new ScmTree(view, this) { Visible = expanded };

		AddChild(Header);
		AddChild(Tree);
	}

	public IGitRepository Repository { get; }

	public SourceControlView View => _view;

	public RepositoryHeader Header { get; }

	public ScmTree Tree { get; }

	public string Key => Repository.GetRootPath();

	public bool Expanded { get; private set; }

	public float Size { get; set; }

	public void Toggle() => SetExpanded(!Expanded);

	public void SetExpanded(bool expanded) {
		if (Expanded == expanded) return;

		Expanded = expanded;
		Tree.Visible = expanded;
		Header.Repaint();
		_view.SectionsChanged();
	}

	public void RunCommand(string command) => _view.RunCommand(command, Repository);

	public void Sync() {
		Header.Sync(Repository);
		Tree.Invalidate();
		Repaint();
	}

	public override void PerformLayout() {
		float header = RepositoryHeader.HeightFor(Scale);

		Header.SetBounds(0f, 0f, Wide, header);
		Tree.SetBounds(0f, header, Wide, Math.Max(0f, Tall - header));
	}
}
