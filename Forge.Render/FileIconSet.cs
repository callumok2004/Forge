using D2D = Vortice.Direct2D1;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class FileIconSet : IFileIconSet, IDisposable
{
	private const int DefaultFileSlot = -1;
	private const int DefaultFolderSlot = -2;
	private const int DefaultFolderExpandedSlot = -3;

	private readonly D2D.ID2D1Factory1 _factory;
	private readonly string _iconDirectory;

	private volatile IconMap? _map;

	private readonly Dictionary<int, VectorIcon?> _cache = [];
	private readonly HashSet<int> _pending = [];
	private readonly System.Collections.Concurrent.ConcurrentQueue<(int Key, string? Svg)> _loaded = new();
	private int _readySignalled;

	private bool _disposed;

	public FileIconSet(D2D.ID2D1Factory1 factory, string assetDirectory) {
		_factory = factory;
		_iconDirectory = Path.Combine(assetDirectory, "icons");

		string mapPath = Path.Combine(assetDirectory, "material-icons.map");

		Task.Run(() => {
			try {
				_map = IconMap.Load(mapPath);
				_ready?.Invoke();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			}
		});
	}

	private Action? _ready;

	public void OnReady(Action handler) {
		_ready -= handler;
		_ready += handler;
	}

	public bool IsReady() => _map is not null;

	public long GetMapBytes() => _map?.ApproximateBytes ?? 0;

	public IVectorIcon? IconForFile(ReadOnlySpan<char> fileName) => ForFile(fileName);

	public VectorIcon? ForFile(ReadOnlySpan<char> fileName) {
		if (_map is not { } map) return null;

		int line = map.FindFile(fileName);

		return line >= 0 ? Resolve(map, line) : Resolve(map.DefaultFile, DefaultFileSlot);
	}

	public VectorIcon? ForFolder(ReadOnlySpan<char> folderName, bool isExpanded) {
		if (_map is not { } map) return null;

		int line = map.FindFolder(folderName, isExpanded);

		if (line >= 0) return Resolve(map, line);

		return isExpanded
			? Resolve(map.DefaultFolderExpanded, DefaultFolderExpandedSlot)
			: Resolve(map.DefaultFolder, DefaultFolderSlot);
	}

	private VectorIcon? Resolve(IconMap map, int line) {
		int offset = map.IconOffset(line);

		return offset == 0 ? null : Resolve(map.IconName(line), offset);
	}

	private VectorIcon? Resolve(string name, int key) {
		DrainLoaded();

		if (_cache.TryGetValue(key, out VectorIcon? cached)) return cached;

		if (!_pending.Add(key)) return null;

		string path = Path.Combine(_iconDirectory, name + ".svg");

		ThreadPool.UnsafeQueueUserWorkItem(_ => {
			string? svg = null;

			try {
				svg = File.ReadAllText(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			}

			_loaded.Enqueue((key, svg));

			if (Interlocked.Exchange(ref _readySignalled, 1) == 0) _ready?.Invoke();
		}, null);

		return null;
	}

	private void DrainLoaded() {
		Volatile.Write(ref _readySignalled, 0);

		while (_loaded.TryDequeue(out (int Key, string? Svg) loaded)) {
			_pending.Remove(loaded.Key);
			_cache[loaded.Key] = loaded.Svg is { } svg ? VectorIcon.Parse(_factory, svg) : null;
		}
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		foreach (VectorIcon? icon in _cache.Values) icon?.Dispose();

		_cache.Clear();
	}
}
