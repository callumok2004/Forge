using Forge.Common.Commands;
using Forge.Common.Workspace;

namespace Forge.Extensions.Dotnet.Solution;

public sealed class SolutionGrouping(SolutionWorkspace workspace, DotnetOutput output)
{
	private bool _grouped;

	public void Apply(ICommandHost host, bool enabled) {
		if (!enabled || workspace.Solution is not { } solutionPath || host.GetWorkspacePath() is not { } root || !IsUnder(solutionPath, root)) {
			Clear(host);
			return;
		}

		try {
			SolutionModel solution = SolutionParser.Parse(solutionPath);
			host.SetExplorerGroups(ToGroups(solution));
			_grouped = true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or System.Text.Json.JsonException or InvalidDataException) {
			output.Log(host, $"failed to parse {solutionPath}: {exception.Message}");
			Clear(host);
		}
	}

	public static bool IsSolutionFile(ReadOnlySpan<char> path) {
		ReadOnlySpan<char> extension = Path.GetExtension(path);
		return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase);
	}

	private void Clear(ICommandHost host) {
		if (!_grouped) return;

		host.SetExplorerGroups(null);
		_grouped = false;
	}

	private static bool IsUnder(string path, string root) {
		ReadOnlySpan<char> trimmed = root.AsSpan().TrimEnd(['\\', '/']);
		return path.Length > trimmed.Length && path.AsSpan().StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) && path[trimmed.Length] is '\\' or '/';
	}

	private static List<ExplorerGroup> ToGroups(SolutionModel solution) {
		List<ExplorerGroup> groups = [];
		List<string> loose = [];

		foreach (SolutionNode node in solution.Roots) {
			if (node.Kind == SolutionNodeKind.Folder) groups.Add(ToGroup(node));
			else if (ResourceOf(node) is { } resource) loose.Add(resource);
		}

		if (loose.Count > 0) groups.Add(new ExplorerGroup(solution.Name, [], loose));

		return groups;
	}

	private static ExplorerGroup ToGroup(SolutionNode folder) {
		List<ExplorerGroup> children = [];
		List<string> resources = [];

		foreach (SolutionNode child in folder.Children) {
			if (child.Kind == SolutionNodeKind.Folder) children.Add(ToGroup(child));
			else if (ResourceOf(child) is { } resource) resources.Add(resource);
		}

		return new ExplorerGroup(folder.Name, children, resources);
	}

	private static string? ResourceOf(SolutionNode node) => node.Kind switch {
		SolutionNodeKind.Project => Path.GetDirectoryName(node.Path),
		SolutionNodeKind.File => node.Path,
		_ => null,
	};
}
