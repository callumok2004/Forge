namespace Forge.Extensions.DotnetProfiler.Engine;

public sealed class FunctionRow(int id, string key, string name, string module, bool system)
{
	public int Id { get; } = id;

	public string Key { get; } = key;

	public string Name { get; } = name;

	public string Module { get; } = module;

	public bool System { get; } = system;

	public long Self { get; internal set; }

	public long Own { get; internal set; }

	public long Total { get; internal set; }

	public List<int> Nodes { get; } = [];
}

public readonly record struct Edge(int Row, string Name, string Module, long Weight);

public readonly record struct CallPath(IReadOnlyList<string> Frames, long Self, long Total);

public sealed class FunctionIndex
{
	public const string SelfName = "(self)";
	public const string RootName = "(thread root)";

	private readonly ProfileTree _tree;
	private readonly int[] _rowOf;
	private readonly bool[] _recursive;
	private readonly List<FunctionRow> _rows = [];
	private readonly Dictionary<string, FunctionRow> _byKey = new(StringComparer.Ordinal);

	public FunctionIndex(ProfileTree tree, bool grouped) {
		_tree = tree;
		Grouped = grouped;

		int count = tree.Count;
		_rowOf = new int[count];
		_recursive = new bool[count];
		_rowOf[0] = -1;

		Dictionary<string, string> normalized = new(StringComparer.Ordinal);
		bool[] frameSystem = new bool[tree.Names.Length];
		int[] frameRow = new int[tree.Names.Length];

		for (int frame = 0; frame < tree.Names.Length; frame++) {
			string name = tree.Names[frame];
			string module = tree.Modules[frame];
			string display = grouped ? Normalized(normalized, name) : name;
			string key = string.Concat(module, "!", display);
			frameSystem[frame] = FrameNames.IsSystem(name, module);

			if (!_byKey.TryGetValue(key, out FunctionRow? row)) {
				row = new FunctionRow(_rows.Count, key, display, module, frameSystem[frame]);
				_rows.Add(row);
				_byKey[key] = row;
			}

			frameRow[frame] = row.Id;
		}

		for (int node = 1; node < count; node++) _rowOf[node] = frameRow[tree.Frame[node]];

		int[] onStack = new int[_rows.Count];
		Stack<(int Node, int Owner, bool Exit)> work = new();

		for (int child = tree.FirstChild[0]; child >= 0; child = tree.NextSibling[child]) work.Push((child, -1, false));

		while (work.Count > 0) {
			(int node, int owner, bool exit) = work.Pop();
			int rowId = _rowOf[node];

			if (exit) {
				onStack[rowId]--;
				continue;
			}

			FunctionRow row = _rows[rowId];
			_recursive[node] = onStack[rowId] > 0;
			row.Self += tree.Self[node];
			row.Nodes.Add(node);

			if (!_recursive[node]) row.Total += tree.Total[node];

			bool system = frameSystem[tree.Frame[node]];
			int ownerRow = system && owner >= 0 ? owner : rowId;
			_rows[ownerRow].Own += tree.Self[node];

			int nextOwner = system ? owner : rowId;
			onStack[rowId]++;
			work.Push((node, owner, true));

			for (int child = tree.FirstChild[node]; child >= 0; child = tree.NextSibling[child]) work.Push((child, nextOwner, false));
		}
	}

	private static string Normalized(Dictionary<string, string> cache, string name) {
		if (cache.TryGetValue(name, out string? value)) return value;

		value = FrameNames.Normalize(name);
		cache[name] = value;
		return value;
	}

	public bool Grouped { get; }

	public ProfileTree Tree => _tree;

	public long RootTotal => _tree.RootTotal;

	public IReadOnlyList<FunctionRow> Rows => _rows;

	public FunctionRow? Get(string key) => _byKey.GetValueOrDefault(key);

	public FunctionRow? RowOfNode(int node) => node > 0 ? _rows[_rowOf[node]] : null;

	public List<Edge> Callers(FunctionRow row) {
		Dictionary<int, long> weights = [];
		long fromRoot = 0;

		foreach (int node in row.Nodes) {
			if (_recursive[node]) continue;

			int parent = _tree.Parent[node];

			if (parent <= 0) {
				fromRoot += _tree.Total[node];
				continue;
			}

			int caller = _rowOf[parent];
			weights[caller] = weights.GetValueOrDefault(caller) + _tree.Total[node];
		}

		List<Edge> edges = new(weights.Count + 1);

		foreach ((int caller, long weight) in weights) edges.Add(new Edge(caller, _rows[caller].Name, _rows[caller].Module, weight));

		if (fromRoot > 0) edges.Add(new Edge(-1, RootName, string.Empty, fromRoot));

		edges.Sort(static (left, right) => right.Weight.CompareTo(left.Weight));
		return edges;
	}

	public List<Edge> Callees(FunctionRow row) {
		Dictionary<int, long> weights = [];
		long self = 0;

		foreach (int node in row.Nodes) {
			self += _tree.Self[node];

			if (_recursive[node]) continue;

			for (int child = _tree.FirstChild[node]; child >= 0; child = _tree.NextSibling[child]) {
				int callee = _rowOf[child];

				if (callee == row.Id) continue;

				weights[callee] = weights.GetValueOrDefault(callee) + _tree.Total[child];
			}
		}

		List<Edge> edges = new(weights.Count + 1);

		foreach ((int callee, long weight) in weights) edges.Add(new Edge(callee, _rows[callee].Name, _rows[callee].Module, weight));

		edges.Sort(static (left, right) => right.Weight.CompareTo(left.Weight));

		if (self > 0) edges.Insert(0, new Edge(row.Id, SelfName, string.Empty, self));

		return edges;
	}

	public List<CallPath> CallPaths(FunctionRow row, int limit = 6, int maximumDepth = 32) {
		int[] nodes = [.. row.Nodes];
		Array.Sort(nodes, (left, right) => {
			int self = _tree.Self[right].CompareTo(_tree.Self[left]);

			return self != 0 ? self : _tree.Total[right].CompareTo(_tree.Total[left]);
		});

		List<CallPath> paths = new(Math.Min(limit, nodes.Length));

		for (int i = 0; i < nodes.Length && i < limit; i++) paths.Add(new CallPath(_tree.StackOf(nodes[i], maximumDepth), _tree.Self[nodes[i]], _tree.Total[nodes[i]]));

		return paths;
	}

	public List<int> HotPath() {
		List<int> path = [];

		for (int node = _tree.FirstChild[0]; node >= 0; node = _tree.FirstChild[node]) path.Add(node);

		return path;
	}
}
