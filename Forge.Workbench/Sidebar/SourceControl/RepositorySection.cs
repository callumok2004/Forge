using Forge.Common.Git;
using Forge.Common.UI;

namespace Forge.Workbench;

public sealed class RepositorySection : Panel
{
	private readonly SourceControlView _view;
	private readonly RepositoryHeader _header;

	public RepositorySection(SourceControlView view) {
		_view = view;
		Name = "RepositorySection";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_header = new RepositoryHeader(this);
		Tree = new ScmTree(view);

		AddChild(_header);
		AddChild(Tree);
	}

	public ScmTree Tree { get; }

	private string Key => $"section:{_view.RootPath}";

	public bool Expanded => !_view.IsCollapsed(Key);

	public void Toggle() {
		_view.Toggle(Key);
		Tree.Visible = Expanded;
		_header.Repaint();
	}

	public void Sync(IGitRepository repository) {
		_header.Sync(repository);
		Tree.Visible = Expanded;
		Tree.Invalidate();
		Repaint();
	}

	public override void PerformLayout() {
		_header.SetSize(Wide, RepositoryHeader.HeightFor(Scale));
		base.PerformLayout();
	}
}
