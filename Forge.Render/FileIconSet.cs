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

		if (offset == 0) return null;

		if (_cache.TryGetValue(offset, out VectorIcon? cached)) return cached;

		VectorIcon? icon = Load(map.IconName(line));

		_cache[offset] = icon;
		return icon;
	}

	private VectorIcon? Resolve(string name, int slot) {
		if (_cache.TryGetValue(slot, out VectorIcon? cached)) return cached;

		VectorIcon? icon = Load(name);

		_cache[slot] = icon;
		return icon;
	}

	private VectorIcon? Load(string name) {
		try {
			return VectorIcon.Load(_factory, Path.Combine(_iconDirectory, name + ".svg"));
		}
		catch (Exception) {
			return null;
		}
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		foreach (VectorIcon? icon in _cache.Values) icon?.Dispose();

		_cache.Clear();
	}
}
