using Forge.Common.Render;

namespace Forge.Common.UI;

public enum ViewLocation
{
	Sidebar,
	Panel,
	Auxiliary,
}

public sealed record ViewContainer(string Id, string Title, Codicon Icon, ViewLocation Location, int Order = 100)
{
	public IVectorIcon? VectorIcon { get; init; }

	public bool Movable { get; init; } = true;
}

public sealed record ViewDescriptor(string Id, string Name, string ContainerId, Func<Panel> Create, int Order = 100)
{
	public IReadOnlyList<ViewAction> Actions { get; init; } = [];

	public Func<IReadOnlyList<Panel>>? TitleControls { get; init; }
}

public sealed record ViewAction(string Command, Codicon Icon, string Title)
{
	public Func<bool>? Visible { get; init; }
}

public abstract class EditorPane : Panel
{
	public string Id { get; set; } = string.Empty;

	public abstract string Title { get; }

	public virtual Codicon Icon => Codicon.File;

	public virtual void OnClosed() { }
}

public interface IWorkbenchViews
{
	void RegisterContainer(ViewContainer container);

	void RegisterView(ViewDescriptor view);

	void ShowContainer(string containerId);

	bool IsContainerVisible(string containerId);

	T OpenEditor<T>(string id, Func<T> create, bool preserveFocus = false) where T : EditorPane;

	T? FindEditor<T>(string id) where T : EditorPane;

	void CloseEditor(string id);

	void Post(Action action);

	event Action? Changed;
}
