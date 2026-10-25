using Forge.Common.Git;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class RepositoryHeader : Panel
{
	private const float Height = 22f;
	private const float TwistieWidth = 20f;
	private const float IconSize = 16f;
	private const float IconPadding = 2f;
	private const float ActionSize = 20f;
	private const float ActionGap = 4f;
	private const float PaddingRight = 8f;
	private const float CountMargin = 6f;

	private readonly RepositorySection _section;
	private readonly Label _name;
	private readonly Button _branch;
	private readonly Button _sync;
	private readonly ActionButton _commit;
	private readonly ActionButton _refresh;
	private readonly ActionButton _more;
	private readonly CountBadge _count;

	public RepositoryHeader(RepositorySection section) {
		_section = section;
		Name = "RepositoryHeader";
		Dock = DockType.Top;
		Cursor = CursorCode.Hand;

		_count = Add(new CountBadge { Name = "Count", Dock = DockType.Right });
		_more = Add(Action(Codicon.Ellipsis, "More Actions...", null));
		_more.Action = () => OpenMore();
		_refresh = Add(Action(Codicon.Refresh, "Refresh", "git.refresh"));
		_commit = Add(Action(Codicon.Check, "Commit", "git.commit"));
		_sync = Add(StatusAction("Sync", "git.sync"));
		_branch = Add(StatusAction("Branch", "git.checkout"));
		_name = Add(new Label { Name = "RepositoryName", Dock = DockType.Fill, Foreground = Theme.Foreground });
	}

	public static float HeightFor(float scale) => Height * scale;

	private T Add<T>(T child) where T : Panel {
		AddChild(child);
		return child;
	}

	private static ActionButton Action(Codicon icon, string name, string? command) => new() {
		Name = name,
		Command = command,
		Icon = icon,
		IconSize = IconSize,
		Dock = DockType.Right,
		Foreground = Theme.Foreground,
		Radius = Metrics.RadiusSmall,
	};

	private static Button StatusAction(string name, string command) => new() {
		Name = name,
		Command = command,
		Dock = DockType.Right,
		Foreground = Theme.Foreground,
		Radius = Metrics.RadiusSmall,
		Padding = new Thickness(IconPadding, 0f, IconPadding, 0f),
		SizeToContents = true,
	};

	private void OpenMore() {
		(float x, float y) = _more.GetAbsPos();
		ApplicationMenu.Build(GitMenus.More).Open(x, y + _more.Tall);
	}

	public void Sync(IGitRepository repository) {
		Span<char> buffer = stackalloc char[128];

		ReadOnlySpan<char> root = repository.GetRootPath().AsSpan().TrimEnd(Path.DirectorySeparatorChar);
		_name.SetText(Path.GetFileName(root));

		string branch = repository.GetBranch() ?? "HEAD";
		int written = Write(buffer, "$(git-branch) ");
		written += Write(buffer[written..], branch.AsSpan(0, Math.Min(branch.Length, 96)));

		if (repository.GetUnstaged().Count > 0) buffer[written++] = '*';

		if (repository.GetStaged().Count > 0) buffer[written++] = '+';

		if (repository.GetMerge().Count > 0) buffer[written++] = '!';

		_branch.SetText(buffer[..written]);

		written = Write(buffer, "$(sync) ");
		repository.GetBehind().TryFormat(buffer[written..], out int digits);
		written += digits;
		written += Write(buffer[written..], "↓ ");
		repository.GetAhead().TryFormat(buffer[written..], out digits);
		written += digits;
		written += Write(buffer[written..], "↑");
		_sync.SetText(buffer[..written]);

		_count.Count = repository.GetStaged().Count + repository.GetUnstaged().Count + repository.GetMerge().Count;
	}

	private static int Write(Span<char> destination, ReadOnlySpan<char> text) {
		text.CopyTo(destination);
		return text.Length;
	}

	public override void PerformLayout() {
		float scale = Scale;
		float height = Height * scale;

		DockPadding = new Thickness((TwistieWidth + IconSize + IconPadding + Metrics.Space4) * scale, 0f, PaddingRight * scale, 0f);

		_count.SetSize(_count.PreferredWidth, height);
		_count.DockMargin = new Thickness(CountMargin * scale, 0f, 0f, 0f);

		foreach (ActionButton action in (ReadOnlySpan<ActionButton>)[_commit, _refresh, _more]) {
			action.SetSize(ActionSize * scale, ActionSize * scale);
			action.DockMargin = new Thickness(ActionGap * scale, (height - (ActionSize * scale)) / 2f, 0f, (height - (ActionSize * scale)) / 2f);
		}

		foreach (Button status in (ReadOnlySpan<Button>)[_sync, _branch]) {
			status.FitContents();
			status.SetSize(status.Wide, ActionSize * scale);
			status.DockMargin = new Thickness(ActionGap * scale, (height - (ActionSize * scale)) / 2f, 0f, (height - (ActionSize * scale)) / 2f);
		}

		base.PerformLayout();
	}

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() => Repaint();

	public override bool OnMousePressed(MouseButton button, float x, float y) => true;

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (x >= 0f && y >= 0f && x < Wide && y < Tall) _section.Toggle();

		return true;
	}

	public override void PaintBackground(IPainter painter) {
		if (IsCursorOver) painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.ListHoverBackground);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float centreY = ScreenY + (Tall / 2f);
		float iconSize = IconSize * scale;

		painter.DrawIcon(_section.Expanded ? Codicon.ChevronDown : Codicon.ChevronRight, ScreenX + (TwistieWidth * scale / 2f), centreY, iconSize, Theme.Foreground);
		painter.DrawIcon(Codicon.Repo, ScreenX + (TwistieWidth * scale) + (iconSize / 2f), centreY, iconSize, Theme.Foreground);
	}
}
