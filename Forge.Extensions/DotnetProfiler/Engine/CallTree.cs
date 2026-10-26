using System.Runtime.InteropServices;

namespace Forge.Extensions.DotnetProfiler.Engine;

public sealed class FrameTable
{
	private readonly Dictionary<(string Module, string Name), int> _index = [];
	private readonly List<string> _names = [];
	private readonly List<string> _modules = [];

	public int Count => _names.Count;

	public int Intern(string module, string name) {
		ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_index, (module, name), out bool exists);

		if (exists) return slot;

		slot = _names.Count;
		_names.Add(name);
		_modules.Add(module);
		return slot;
	}

	public string[] Names() => [.. _names];

	public string[] Modules() => [.. _modules];
}

public sealed class CallTree
{
	private const int Root = 0;

	private readonly Dictionary<long, int> _children = [];

	private int[] _parent;
	private int[] _frame;
	private long[] _self;
	private long[] _total;
	private int _count;

	public CallTree(int capacity = 4096) {
		_parent = new int[capacity];
		_frame = new int[capacity];
		_self = new long[capacity];
		_total = new long[capacity];
		_parent[0] = -1;
		_frame[0] = -1;
		_count = 1;
	}

	public FrameTable Frames { get; } = new();

	public long TotalWeight => _total[Root];

	public int Count => _count;

	private int Child(int parent, int frame) {
		long key = ((long)parent << 32) | (uint)frame;
		ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_children, key, out bool exists);

		if (exists) return slot;

		if (_count == _parent.Length) {
			int size = _count * 2;
			Array.Resize(ref _parent, size);
			Array.Resize(ref _frame, size);
			Array.Resize(ref _self, size);
			Array.Resize(ref _total, size);
		}

		int id = _count++;
		_parent[id] = parent;
		_frame[id] = frame;
		slot = id;
		return id;
	}

	public int AddSample(ReadOnlySpan<int> leafToRoot, long weight = 1) {
		if (leafToRoot.IsEmpty) return Root;

		int node = Root;
		_total[Root] += weight;

		for (int i = leafToRoot.Length - 1; i >= 0; i--) {
			node = Child(node, leafToRoot[i]);
			_total[node] += weight;
		}

		_self[node] += weight;
		return node;
	}

	public ProfileTree Snapshot() => new(_parent.AsSpan(0, _count).ToArray(), _frame.AsSpan(0, _count).ToArray(), _self.AsSpan(0, _count).ToArray(), _total.AsSpan(0, _count).ToArray(), Frames.Names(), Frames.Modules());
}

public sealed class ProfileTree
{
	public ProfileTree(int[] parent, int[] frame, long[] self, long[] total, string[] names, string[] modules) {
		Parent = parent;
		Frame = frame;
		Self = self;
		Total = total;
		Names = names;
		Modules = modules;

		int count = parent.Length;
		FirstChild = new int[count];
		NextSibling = new int[count];
		Array.Fill(FirstChild, -1);
		Array.Fill(NextSibling, -1);

		int[] order = new int[Math.Max(0, count - 1)];

		for (int i = 1; i < count; i++) order[i - 1] = i;

		Array.Sort(order, (left, right) => total[left].CompareTo(total[right]));

		foreach (int node in order) {
			int up = parent[node];
			NextSibling[node] = FirstChild[up];
			FirstChild[up] = node;
		}
	}

	public static ProfileTree Empty { get; } = new([-1], [-1], [0], [0], [], []);

	public int[] Parent { get; }

	public int[] Frame { get; }

	public long[] Self { get; }

	public long[] Total { get; }

	public int[] FirstChild { get; }

	public int[] NextSibling { get; }

	public string[] Names { get; }

	public string[] Modules { get; }

	public int Count => Parent.Length;

	public long RootTotal => Total[0];

	public string Name(int node) => Names[Frame[node]];

	public string Module(int node) => Modules[Frame[node]];

	public int ChildCount(int node) {
		int count = 0;

		for (int child = FirstChild[node]; child >= 0; child = NextSibling[child]) count++;

		return count;
	}

	public List<string> StackOf(int node, int maximumDepth = 32) {
		List<string> frames = [];

		for (int n = node; n > 0 && frames.Count < maximumDepth; n = Parent[n]) frames.Add(Name(n));

		return frames;
	}
}
