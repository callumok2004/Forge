namespace Forge.Common.Workspace;

public sealed record ExplorerGroup(string Label, IReadOnlyList<ExplorerGroup> Children, IReadOnlyList<string> Resources);
