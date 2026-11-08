using Forge.Common.Git;
using Forge.Theming;

namespace Forge.Workbench;

public enum ScmGraphKind
{
	Node,
	Head,
	IncomingChanges,
	OutgoingChanges,
}

public readonly record struct ScmGraphLane(string Id, Color Color);

public readonly record struct ScmGraphReference(GitRef Ref, Color? Color);

public sealed record ScmGraphItem(GitCommit Commit, ScmGraphKind Kind, List<ScmGraphLane> Input, List<ScmGraphLane> Output, ScmGraphReference[] References)
{
	public int CircleIndex {
		get {
			int index = Input.FindIndex(lane => lane.Id == Commit.Id);
			return index >= 0 ? index : Input.Count;
		}
	}
}

public static class ScmGraph
{
	public const string IncomingId = "scm-graph-incoming-changes";
	public const string OutgoingId = "scm-graph-outgoing-changes";

	public static readonly Color RefColor = Color.Parse("#3794FF");
	public static readonly Color RemoteRefColor = Color.Parse("#B180D7");
	public static readonly Color BaseRefColor = Color.Parse("#EA5C00");

	public static readonly Color[] Palette = [Color.Parse("#FFB000"), Color.Parse("#DC267F"), Color.Parse("#994F00"), Color.Parse("#40B0A6"), Color.Parse("#B66DFF")];

	private static Color? LabelColor(GitCommit commit, Dictionary<string, Color?> colors) {
		if (commit.Id == IncomingId) return RemoteRefColor;

		if (commit.Id == OutgoingId) return RefColor;

		foreach (GitRef reference in commit.References) {
			if (colors.TryGetValue(reference.Id, out Color? color) && color is not null) return color;
		}

		return null;
	}

	public static List<ScmGraphItem> Build(GitHistory history, bool incoming = true, bool outgoing = true) {
		Dictionary<string, Color?> colors = new(StringComparer.Ordinal);

		if (history.Current is { } current) colors[current.Id] = RefColor;

		if (history.Remote is { } remote) colors[remote.Id] = RemoteRefColor;

		Dictionary<string, GitCommit> byId = new(StringComparer.Ordinal);

		foreach (GitCommit commit in history.Commits) byId.TryAdd(commit.Id, commit);

		int colorIndex = -1;
		List<ScmGraphItem> items = [];

		foreach (GitCommit commit in history.Commits) {
			ScmGraphKind kind = commit.Id == history.CurrentRevision ? ScmGraphKind.Head : ScmGraphKind.Node;
			List<ScmGraphLane> input = items.Count > 0 ? [.. items[^1].Output] : [];
			List<ScmGraphLane> output = [];
			bool firstParentAdded = false;

			if (commit.Parents.Length > 0) {
				foreach (ScmGraphLane lane in input) {
					if (lane.Id == commit.Id) {
						if (!firstParentAdded) {
							output.Add(new ScmGraphLane(commit.Parents[0], LabelColor(commit, colors) ?? lane.Color));
							firstParentAdded = true;
						}

						continue;
					}

					output.Add(lane);
				}
			}

			for (int i = firstParentAdded ? 1 : 0; i < commit.Parents.Length; i++) {
				Color? color = i == 0 ? LabelColor(commit, colors) : byId.TryGetValue(commit.Parents[i], out GitCommit? parent) ? LabelColor(parent, colors) : null;

				if (color is null) {
					colorIndex = (colorIndex + 1) % Palette.Length;
					color = Palette[colorIndex];
				}

				output.Add(new ScmGraphLane(commit.Parents[i], color.Value));
			}

			int circle = input.FindIndex(lane => lane.Id == commit.Id);

			if (circle < 0) circle = input.Count;

			Color circleColor = circle < output.Count ? output[circle].Color : circle < input.Count ? input[circle].Color : RefColor;
			ScmGraphReference[] references = new ScmGraphReference[commit.References.Length];

			for (int i = 0; i < references.Length; i++) {
				GitRef reference = commit.References[i];
				references[i] = new ScmGraphReference(reference, colors.TryGetValue(reference.Id, out Color? color) ? color ?? circleColor : null);
			}

			Array.Sort(references, (left, right) => Order(left, history) - Order(right, history));
			items.Add(new ScmGraphItem(commit, kind, input, output, references));
		}

		AddIncomingOutgoing(items, history, incoming, outgoing);
		return items;
	}

	private static int Order(in ScmGraphReference reference, GitHistory history) {
		if (reference.Ref.Id == history.Current?.Id) return 1;

		if (reference.Ref.Id == history.Remote?.Id) return 2;

		return reference.Color is not null ? 4 : 99;
	}

	private static void AddIncomingOutgoing(List<ScmGraphItem> items, GitHistory history, bool incoming, bool outgoing) {
		if (history.CurrentRevision == history.RemoteRevision || history.MergeBase is not { Length: > 0 } mergeBase) return;

		if (incoming && history.Remote is { } remote && history.RemoteRevision != mergeBase) {
			int before = items.FindLastIndex(item => item.Output.Exists(lane => lane.Id == mergeBase));
			int after = items.FindIndex(item => item.Commit.Id == mergeBase);

			if (before >= 0 && after >= 0) {
				string[] beforeParents = items[before].Commit.Parents;
				bool merged = beforeParents.Length == 2 && Array.IndexOf(beforeParents, mergeBase) >= 0;

				if (!merged) {
					ScmGraphItem previous = items[before];
					items[before] = previous with { Input = Retarget(previous.Input, mergeBase), Output = Retarget(previous.Output, mergeBase) };

					GitCommit commit = new(IncomingId, [mergeBase], remote.Name, string.Empty, 0, "Incoming Changes", string.Empty, []);
					items.Insert(after, new ScmGraphItem(commit, ScmGraphKind.IncomingChanges, [.. items[before].Output], [.. items[after].Input], []));
				}
			}
		}

		if (!outgoing || history.CurrentRevision is not { Length: > 0 } head || head == mergeBase) return;

		int index = items.FindIndex(item => item.Kind == ScmGraphKind.Head && item.Commit.Id == head);

		if (index < 0) return;

		GitCommit outgoingCommit = new(OutgoingId, [head], history.Current?.Name ?? string.Empty, string.Empty, 0, "Outgoing Changes", string.Empty, []);
		List<ScmGraphLane> input = [.. items[index].Input];
		List<ScmGraphLane> output = [.. input, new ScmGraphLane(head, RefColor)];

		items.Insert(index, new ScmGraphItem(outgoingCommit, ScmGraphKind.OutgoingChanges, input, output, []));
		items[index + 1].Input.Add(new ScmGraphLane(head, RefColor));
	}

	private static List<ScmGraphLane> Retarget(List<ScmGraphLane> lanes, string mergeBase) {
		List<ScmGraphLane> result = new(lanes.Count);

		foreach (ScmGraphLane lane in lanes) result.Add(lane.Id == mergeBase && lane.Color.ToArgb() == RemoteRefColor.ToArgb() ? lane with { Id = IncomingId } : lane);

		return result;
	}
}
